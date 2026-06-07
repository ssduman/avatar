using UnityEngine;
using System.Text;
using System.Collections;
using System.Collections.Generic;

namespace ls
{
	[System.Serializable]
	public enum LipSyncMode
	{
		rule_based,
		cmu_dict,
	}

	[System.Serializable]
	public class TranscriptSegment
	{
		public string text;
		public float start;
		public float end;

		public override string ToString()
		{
			return $"[{start:F2} - {end:F2} '{text}']";
		}
	}

	[System.Serializable]
	public class Transcript
	{
		public TranscriptSegment[] segments;

		public override string ToString()
		{
			string s = "";
			foreach (TranscriptSegment segment in segments)
			{
				s += $"{segment.ToString()} ";
			}
			return s;
		}
	}

	[System.Serializable]
	public struct VisemeKey
	{
		public string viseme;
		public float time;
		public string word;
		public string phoneme;
		public int wordIndex;

		public VisemeKey(string viseme, float time)
		{
			this.viseme = viseme;
			this.time = time;
			this.word = "";
			this.phoneme = "";
			this.wordIndex = -1;
		}

		public VisemeKey(string viseme, float time, string word, string phoneme, int wordIndex)
		{
			this.viseme = viseme;
			this.time = time;
			this.word = word;
			this.phoneme = phoneme;
			this.wordIndex = wordIndex;
		}

		public override string ToString()
		{
			return $"'{viseme}' at {time:F2} sec";
		}
	}

	[System.Serializable]
	public struct VisemeSource
	{
		public string viseme;
		public string word;
		public string phoneme;
		public int wordIndex;

		public VisemeSource(string viseme, string word, string phoneme, int wordIndex)
		{
			this.viseme = viseme;
			this.word = word;
			this.phoneme = phoneme;
			this.wordIndex = wordIndex;
		}
	}

	public class LipSyncContext
	{
		// blendshape resolution
		public float[] currentWeights;
		public List<int> lipAndJawIndices = new List<int>();
		public Dictionary<string, int> blendShapeIndex = new Dictionary<string, int>();
		public Dictionary<string, string[]> cmuDict = new Dictionary<string, string[]>();
		public int browIndex = -1;
		public List<int> blinkIndices = new List<int>();

		// head motion
		public Quaternion headRestRot;
		public bool hasHeadRest;
		public float headAmp;
		public float headTalkBlend;

		// blink
		public float nextBlinkTime;
		public float blinkPhase = -1.0f;
		public int extraBlinks;

		// jaw
		public int jawIndex = -1;

		// playback
		public Coroutine playRoutine;
		public float timer;
		public List<VisemeKey> schedule = new List<VisemeKey>();
		public List<string> words = new List<string>();
		public float scheduleEnd;

		// current-frame state
		public int scheduleIndex = -1;
		public string currentViseme;
		public string nextViseme;
		public float blendFactor;
		public string currentWord = "";
		public string currentPhoneme = "";
		public int currentWordIndex = -1;
	}

	public class LipSyncManager : MonoBehaviour
	{
		[Header("Init")]
		public SkinnedMeshRenderer targetRenderer;
		public TextAsset cmuDictAsset;
		public TextAsset transcriptJson;
		public LipSyncMode mode = LipSyncMode.cmu_dict;
		public bool verboseLogging = false;

		[Header("Blend Shapes")]
		[Range(0.0f, 1.0f)] public float jawApplyRatio = 0.1f;
		[Range(0.0f, 1.0f)] public float blendApplyRatio = 0.4f;
		[Range(0.0f, 100.0f)] public float blendSpeed = 28.0f;

		[Header("Brow")]
		public bool enableBrowMotion = true;

		[Header("Eye Blink")]
		public bool enableBlink = true;
		public Vector2 blinkInterval = new Vector2(2.5f, 6.0f);
		[Range(0.0f, 1.0f)] public float doubleBlinkChance = 0.15f;

		[Header("Head Motion")]
		public bool enableHeadMotion = true;
		public Transform headBone;
		[Range(0.0f, 2.0f)] public float headFrequency = 0.4f;
		[Range(0.0f, 10.0f)] public float headIdleAmplitude = 1.5f;
		[Range(0.0f, 10.0f)] public float headTalkAmplitude = 3.5f;

		private readonly LipSyncContext _ctx = new LipSyncContext();

		private static readonly HashSet<string> OpenVowels = new HashSet<string>
		{
			"aa", "oh", "ou",
		};
		private static readonly string[] _visemeKeys = new string[]
		{
			"sil", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "aa", "e", "ih", "oh", "ou",
		};
		private static readonly Dictionary<string, float> VisemeIntensities = new Dictionary<string, float>
		{
			{ "aa", 1.0f }, { "oh", 0.95f }, { "ou", 0.85f },
			{ "e", 0.7f }, { "ih", 0.6f }, { "rr", 0.55f },
			{ "th", 0.5f }, { "dd", 0.45f }, { "nn", 0.45f }, { "kk", 0.45f },
			{ "ch", 0.45f }, { "ss", 0.4f }, { "ff", 0.4f }, { "pp", 0.35f },
		};

		private void Awake()
		{
			Application.targetFrameRate = 60;
			Application.runInBackground = false;
			QualitySettings.vSyncCount = 0;
		}

		private void Start()
		{
			if (targetRenderer == null || targetRenderer.sharedMesh == null)
			{
				Debug.LogError("[LipSync] No SkinnedMeshRenderer with a mesh assigned.", this);
				return;
			}

			_ctx.nextBlinkTime = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);

			LoadCMUDict();
			ResolveBlendShapes();
			CacheBoneRests();
		}

		private void OnEnable()
		{
		}

		private void OnDisable()
		{
			StopAllCoroutines();
		}

		private void Update()
		{
			if (_ctx.currentWeights == null)
			{
				return;
			}
		}

		private void LateUpdate()
		{
			if (_ctx.currentWeights == null)
			{
				return;
			}

			if (enableBlink && _ctx.blinkIndices.Count > 0)
			{
				UpdateBlink();
			}
			if (enableHeadMotion && _ctx.hasHeadRest)
			{
				UpdateHead();
			}
			if (enableBrowMotion && _ctx.browIndex >= 0)
			{
				UpdateBrow();
			}
		}

		#region init

		private void LoadCMUDict()
		{
			if (cmuDictAsset == null)
			{
				Debug.LogWarning("[LipSync] No cmu dict assigned.", this);
				return;
			}

			_ctx.cmuDict.Clear();

			char[] dictSplit = new char[] { ' ', '\t', '\r' };

			string[] lines = cmuDictAsset.text.Split('\n');
			foreach (string raw in lines)
			{
				string line = raw;
				if (line.Length == 0 || line[0] == ';')
				{
					continue;
				}

				int hash = line.IndexOf(" #");
				if (hash >= 0)
				{
					line = line.Substring(0, hash);
				}

				string[] tokens = line.Split(dictSplit, System.StringSplitOptions.RemoveEmptyEntries);
				if (tokens.Length < 2)
				{
					continue;
				}

				string word = tokens[0].ToLowerInvariant();
				int paren = word.IndexOf('(');
				if (paren >= 0)
				{
					word = word.Substring(0, paren);
				}

				if (_ctx.cmuDict.ContainsKey(word))
				{
					continue;
				}

				string[] phonemes = new string[tokens.Length - 1];
				System.Array.Copy(tokens, 1, phonemes, 0, phonemes.Length);
				_ctx.cmuDict[word] = phonemes;
			}

			string sampleWord = "world";
			string sample = _ctx.cmuDict.ContainsKey(sampleWord) ? string.Join(" ", _ctx.cmuDict[sampleWord]) : "(not found)";
			Debug.Log($"[LipSync] Loaded {_ctx.cmuDict.Count} CMUdict entries. {sampleWord}: {sample}", this);
		}

		private void ResolveBlendShapes()
		{
			Mesh mesh = targetRenderer.sharedMesh;

			_ctx.browIndex = -1;
			_ctx.blendShapeIndex.Clear();
			_ctx.blinkIndices.Clear();
			_ctx.lipAndJawIndices.Clear();

			_ctx.currentWeights = new float[mesh.blendShapeCount];

			for (int i = 0; i < mesh.blendShapeCount; i++)
			{
				_ctx.currentWeights[i] = targetRenderer.GetBlendShapeWeight(i);

				string full = mesh.GetBlendShapeName(i);
				string key = ShortName(full);

				foreach (string v in _visemeKeys)
				{
					if (key == v)
					{
						_ctx.lipAndJawIndices.Add(i);
						_ctx.blendShapeIndex[v] = i;
					}
				}

				if (key.StartsWith("jawopen") && !_ctx.blendShapeIndex.ContainsKey("jaw"))
				{
					_ctx.lipAndJawIndices.Add(i);
					_ctx.jawIndex = i;
					_ctx.blendShapeIndex["jaw"] = i;
				}
				if (key.Contains("blink") || key.Contains("eyesclosed") || key.Contains("eyeclose"))
				{
					_ctx.blinkIndices.Add(i);
				}
				if (_ctx.browIndex < 0 && key.Contains("brow") && (key.Contains("up") || key.Contains("raise")))
				{
					_ctx.browIndex = i;
				}
			}

			List<string> missing = new List<string>();
			foreach (string v in _visemeKeys)
			{
				if (!_ctx.blendShapeIndex.ContainsKey(v))
				{
					missing.Add(v);
				}
			}
			if (missing.Count > 0)
			{
				Debug.LogWarning($"[LipSync] Mesh missing visemes (will be skipped): {string.Join(", ", missing)}", this);
			}

			Debug.Log($"[LipSync] Resolved {_ctx.blendShapeIndex.Count} viseme/jaw blendshapes on '{mesh.name}'.", this);
		}

		private string ShortName(string full)
		{
			int dot = full.LastIndexOf('.');
			string s = dot >= 0 ? full.Substring(dot + 1) : full;
			return s.ToLowerInvariant();
		}

		private void CacheBoneRests()
		{
			if (headBone != null)
			{
				_ctx.headRestRot = headBone.localRotation;
				_ctx.hasHeadRest = true;
				_ctx.headAmp = headIdleAmplitude;
				_ctx.headTalkBlend = 0.0f;
			}
		}

		#endregion

		#region playback

		public void Play()
		{
			if (transcriptJson == null)
			{
				Debug.LogWarning("[LipSync] No transcript assigned.", this);
				return;
			}

			Play(transcriptJson.text);
		}

		public void Play(string json)
		{
			if (string.IsNullOrEmpty(json))
			{
				Debug.LogWarning("[LipSync] Empty transcript JSON.", this);
				return;
			}

			Transcript transcript;
			try
			{
				transcript = JsonUtility.FromJson<Transcript>(json);
				if (transcript == null || transcript.segments == null || transcript.segments.Length == 0)
				{
					Debug.LogWarning("[LipSync] Transcript has no segments.", this);
					return;
				}
			}
			catch (System.Exception e)
			{
				Debug.LogError($"[LipSync] Failed to parse transcript JSON: {e.Message}", this);
				return;
			}

			List<VisemeKey> schedule = BuildSchedule(transcript);
			if (schedule.Count == 0)
			{
				return;
			}

			_ctx.schedule = schedule;
			_ctx.scheduleEnd = schedule[schedule.Count - 1].time;

			if (_ctx.playRoutine != null)
			{
				StopCoroutine(_ctx.playRoutine);
			}

			_ctx.playRoutine = StartCoroutine(PlayRoutine(schedule));
		}

		public void Stop()
		{
			if (_ctx.playRoutine != null)
			{
				StopCoroutine(_ctx.playRoutine);
				_ctx.playRoutine = null;
			}

			StartCoroutine(EaseToRest());
		}

		private IEnumerator PlayRoutine(List<VisemeKey> schedule)
		{
			float startTime = Time.time;
			float endTime = schedule[schedule.Count - 1].time;

			int index = 0;

			while (true)
			{
				float t = Time.time - startTime;
				_ctx.timer = t;

				while (index < schedule.Count - 1 && schedule[index + 1].time <= t)
				{
					index++;
				}

				VisemeKey cur = schedule[index];

				_ctx.scheduleIndex = index;
				_ctx.currentViseme = cur.viseme;
				_ctx.currentWord = cur.word;
				_ctx.currentPhoneme = cur.phoneme;
				_ctx.currentWordIndex = cur.wordIndex;

				if (index < schedule.Count - 1)
				{
					VisemeKey next = schedule[index + 1];
					float span = next.time - cur.time;
					float f = span > 0.0001f ? Mathf.Clamp01((t - cur.time) / span) : 1.0f;
					f = f * f * (3.0f - 2.0f * f);
					_ctx.nextViseme = next.viseme;
					_ctx.blendFactor = f;
					ApplyTargets(cur.viseme, 1.0f - f, next.viseme, f);
				}
				else
				{
					_ctx.nextViseme = null;
					_ctx.blendFactor = 0.0f;
					ApplyTargets(cur.viseme, 1.0f, null, 0.0f);
				}

				if (t >= endTime && WeightsSettled())
				{
					break;
				}

				yield return null;
			}

			_ctx.scheduleIndex = -1;
			_ctx.currentViseme = null;
			_ctx.nextViseme = null;
			_ctx.currentWord = "";
			_ctx.currentPhoneme = "";
			_ctx.currentWordIndex = -1;
			_ctx.blendFactor = 0.0f;
			_ctx.playRoutine = null;
		}

		private bool IsTalking()
		{
			return _ctx.playRoutine != null;
		}

		#endregion

		#region shedule

		private List<VisemeKey> BuildSchedule(Transcript transcript)
		{
			List<VisemeKey> keys = new List<VisemeKey>();
			_ctx.words.Clear();
			float prevEnd = 0.0f;
			int wordCounter = 0;

			foreach (TranscriptSegment seg in transcript.segments)
			{
				if (seg == null)
				{
					continue;
				}

				float start = Mathf.Max(0.0f, seg.start);
				float end = Mathf.Max(start, seg.end);

				if (start > prevEnd + 0.01f)
				{
					keys.Add(new VisemeKey("sil", prevEnd));
				}

				List<VisemeSource> visemes = TextToVisemes(seg.text, ref wordCounter);
				if (visemes.Count == 0)
				{
					keys.Add(new VisemeKey("sil", start));
				}
				else
				{
					float duration = Mathf.Max(0.0001f, end - start);
					float step = duration / visemes.Count;
					for (int i = 0; i < visemes.Count; i++)
					{
						VisemeSource vs = visemes[i];
						keys.Add(new VisemeKey(vs.viseme, start + i * step, vs.word, vs.phoneme, vs.wordIndex));
					}
				}

				prevEnd = end;
			}

			keys.Add(new VisemeKey("sil", prevEnd));

			_ctx.words.Clear();
			int expected = 0;
			foreach (VisemeKey k in keys)
			{
				if (k.wordIndex == expected && !string.IsNullOrEmpty(k.word))
				{
					_ctx.words.Add(k.word);
					expected++;
				}
			}

			if (verboseLogging)
			{
				StringBuilder sbVisemes = new StringBuilder();
				foreach (VisemeKey k in keys)
				{
					sbVisemes.Append($" '{k.viseme}' at {k.time:F2} sec |");
				}
				Debug.Log($"[LipSync] text: {transcript}, visemes: {sbVisemes.ToString()}", this);
			}

			return keys;
		}

		#endregion

		#region viseme

		private List<VisemeSource> TextToVisemes(string text, ref int wordCounter)
		{
			List<VisemeSource> result = new List<VisemeSource>();
			if (string.IsNullOrEmpty(text))
			{
				return result;
			}

			string lower = text.ToLowerInvariant();
			string[] words = SplitWords(lower);

			for (int w = 0; w < words.Length; w++)
			{
				string word = words[w];
				if (word.Length == 0)
				{
					continue;
				}

				int wordIndex = wordCounter++;

				if (mode == LipSyncMode.cmu_dict && _ctx.cmuDict != null && _ctx.cmuDict.TryGetValue(word, out string[] phonemes))
				{
					foreach (string ph in phonemes)
					{
						AppendPhonemeViseme(ph, result, word, wordIndex);
					}
				}
				else
				{
					AppendWordVisemesByRules(word, result, wordIndex);
				}

				if (w < words.Length - 1)
				{
					AddViseme(result, "sil", "", "", -1);
				}
			}

			return result;
		}

		private void AppendPhonemeViseme(string phoneme, List<VisemeSource> result, string word, int wordIndex)
		{
			string p = phoneme.TrimEnd('0', '1', '2');
			switch (p)
			{
				// diphthongs -> two shapes
				case "AW": AddViseme(result, "aa", word, p, wordIndex); AddViseme(result, "ou", word, p, wordIndex); return; // how
				case "AY": AddViseme(result, "aa", word, p, wordIndex); AddViseme(result, "ih", word, p, wordIndex); return; // my
				case "OY": AddViseme(result, "oh", word, p, wordIndex); AddViseme(result, "ih", word, p, wordIndex); return; // boy
				case "EY": AddViseme(result, "e", word, p, wordIndex); AddViseme(result, "ih", word, p, wordIndex); return; // say
				case "OW": AddViseme(result, "oh", word, p, wordIndex); return; // go

				// monophthong vowels
				case "AA": case "AO": case "AE": case "AH": case "AX": AddViseme(result, "aa", word, p, wordIndex); return;
				case "EH": case "ER": AddViseme(result, "e", word, p, wordIndex); return;
				case "IH": case "IY": AddViseme(result, "ih", word, p, wordIndex); return;
				case "UH": case "UW": AddViseme(result, "ou", word, p, wordIndex); return;

				// consonants
				case "P": case "B": case "M": AddViseme(result, "pp", word, p, wordIndex); return;
				case "F": case "V": AddViseme(result, "ff", word, p, wordIndex); return;
				case "TH": case "DH": AddViseme(result, "th", word, p, wordIndex); return;
				case "T": case "D": case "L": case "DX": AddViseme(result, "dd", word, p, wordIndex); return;
				case "N": AddViseme(result, "nn", word, p, wordIndex); return;
				case "NG": AddViseme(result, "nn", word, p, wordIndex); return;
				case "K": case "G": AddViseme(result, "kk", word, p, wordIndex); return;
				case "CH": case "JH": case "SH": case "ZH": AddViseme(result, "ch", word, p, wordIndex); return;
				case "S": case "Z": AddViseme(result, "ss", word, p, wordIndex); return;
				case "R": AddViseme(result, "rr", word, p, wordIndex); return;

				// glides / aspirate -> no viseme
				case "Y": case "W": case "HH": return;

				default: return;
			}
		}

		private void AppendWordVisemesByRules(string word, List<VisemeSource> result, int wordIndex)
		{
			int i = 0;
			while (i < word.Length)
			{
				char c = word[i];
				char next = i + 1 < word.Length ? word[i + 1] : '\0';

				bool isLast = i == word.Length - 1;

				// consonant digraphs (checked first so 'wh'/'qu' win over glide/vowel rules)
				if (c == 't' && next == 'h') { AddViseme(result, "th", word, "th", wordIndex); i += 2; continue; }
				if (c == 's' && next == 'h') { AddViseme(result, "ch", word, "sh", wordIndex); i += 2; continue; }
				if (c == 'c' && next == 'h') { AddViseme(result, "ch", word, "ch", wordIndex); i += 2; continue; }
				if (c == 'p' && next == 'h') { AddViseme(result, "ff", word, "ph", wordIndex); i += 2; continue; }
				if (c == 'n' && next == 'g') { AddViseme(result, "nn", word, "ng", wordIndex); i += 2; continue; }
				if (c == 'c' && next == 'k') { AddViseme(result, "kk", word, "ck", wordIndex); i += 2; continue; }
				if (c == 'q' && next == 'u') { AddViseme(result, "kk", word, "qu", wordIndex); AddViseme(result, "ou", word, "qu", wordIndex); i += 2; continue; }
				if (c == 'w' && next == 'h') { AddViseme(result, "ou", word, "wh", wordIndex); i += 2; continue; }

				// vowel digraphs / diphthongs (map to the dominant mouth shape)
				if (c == 'o' && next == 'w') { AddViseme(result, "ou", word, "ow", wordIndex); i += 2; continue; } // how, now
				if (c == 'o' && next == 'u') { AddViseme(result, "ou", word, "ou", wordIndex); i += 2; continue; } // you, soup
				if (c == 'o' && next == 'o') { AddViseme(result, "ou", word, "oo", wordIndex); i += 2; continue; } // food, moon
				if (c == 'o' && next == 'a') { AddViseme(result, "oh", word, "oa", wordIndex); i += 2; continue; } // boat
				if (c == 'o' && (next == 'i' || next == 'y')) { AddViseme(result, "oh", word, "oi", wordIndex); AddViseme(result, "ih", word, "oi", wordIndex); i += 2; continue; } // boy, coin
				if (c == 'e' && (next == 'e' || next == 'a' || next == 'i')) { AddViseme(result, "e", word, "e" + next, wordIndex); i += 2; continue; } // see, eat, vein
				if (c == 'a' && (next == 'i' || next == 'y')) { AddViseme(result, "e", word, "a" + next, wordIndex); i += 2; continue; } // rain, day
				if (c == 'a' && (next == 'u' || next == 'w')) { AddViseme(result, "aa", word, "a" + next, wordIndex); i += 2; continue; } // saw, caught
				if (c == 'u' && next == 'e') { AddViseme(result, "ou", word, "ue", wordIndex); i += 2; continue; } // blue, true
				if (c == 'i' && next == 'e') { AddViseme(result, "ih", word, "ie", wordIndex); i += 2; continue; } // pie, tie

				// leading glide y/w: consonant onset, no viseme of its own (you, we)
				if (i == 0 && (c == 'y' || c == 'w') && word.Length > 1) { i++; continue; }

				// silent trailing 'e' (are, name, like) - keep short words (be, he)
				if (c == 'e' && isLast && word.Length > 2) { i++; continue; }

				// single letters
				string v = LetterToViseme(c);
				if (!string.IsNullOrEmpty(v))
				{
					AddViseme(result, v, word, c.ToString(), wordIndex);
				}

				i++;
			}
		}

		private string LetterToViseme(char c)
		{
			switch (c)
			{
				// labials
				case 'p':
				case 'b':
				case 'm': return "pp";

				// labiodental
				case 'f':
				case 'v': return "ff";

				// alveolar stops/laterals
				case 't':
				case 'd':
				case 'l': return "dd";
				case 'n': return "nn";

				// velars
				case 'k':
				case 'g':
				case 'c': return "kk";
				case 'j': return "ch";

				// sibilants
				case 's':
				case 'z':
				case 'x': return "ss";
				case 'r': return "rr";

				// vowels
				case 'a': return "aa";
				case 'e': return "e";
				case 'i':
				case 'y': return "ih";
				case 'o': return "oh";
				case 'u': return "ou";

				// glides / silent-ish: skip so neighbours dominate
				case 'h':
				case 'w': return null;

				default: return null;
			}
		}

		private void AddViseme(List<VisemeSource> list, string v, string word, string phoneme, int wordIndex)
		{
			if (list.Count > 0 && list[list.Count - 1].viseme == v)
			{
				return;
			}
			list.Add(new VisemeSource(v, word, phoneme, wordIndex));
		}

		private string[] SplitWords(string lower)
		{
			StringBuilder sb = new StringBuilder(lower.Length);
			foreach (char c in lower)
			{
				sb.Append(c >= 'a' && c <= 'z' ? c : ' ');
			}
			return sb.ToString().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
		}

		#endregion

		#region apply

		private void ApplyTargets(string viseme)
		{
			ApplyTargets(viseme, 1.0f, null, 0.0f);
		}

		private void ApplyTargets(string visemeA, float weightA, string visemeB, float weightB)
		{
			ResolveVisemeAndGetIndex(visemeA, out int idxA, out float shapeA);
			ResolveVisemeAndGetIndex(visemeB, out int idxB, out float shapeB);
			
			shapeA *= weightA;
			shapeB *= weightB;

			float jawWeight = 0.0f;
			float jawIntensity = 0.0f;

			if (visemeA != null)
			{
				if (OpenVowels.Contains(visemeA))
				{
					jawWeight = Mathf.Max(jawWeight, weightA);
				}
				if (VisemeIntensities.ContainsKey(visemeA))
				{
					jawIntensity = Mathf.Max(jawIntensity, VisemeIntensities[visemeA]);
				}
			}

			if (visemeB != null)
			{
				if (OpenVowels.Contains(visemeB))
				{
					jawWeight = Mathf.Max(jawWeight, weightB);
				}
				if (VisemeIntensities.ContainsKey(visemeB))
				{
					jawIntensity = Mathf.Max(jawIntensity, VisemeIntensities[visemeB]);
				}
			}

			float jawTarget = jawWeight * jawIntensity * jawApplyRatio * 100.0f;

			float scaleA = shapeA * blendApplyRatio * 100.0f;
			float scaleB = shapeB * blendApplyRatio * 100.0f;

			for (int i = 0; i < _ctx.lipAndJawIndices.Count; i++)
			{
				int index = _ctx.lipAndJawIndices[i];

				float target = 0.0f;
				if (index == idxA)
				{
					target = Mathf.Max(target, scaleA);
				}
				if (index == idxB)
				{
					target = Mathf.Max(target, scaleB);
				}
				if (index == _ctx.jawIndex)
				{
					target = Mathf.Max(target, jawTarget);
				}

				if (!Mathf.Approximately(_ctx.currentWeights[index], target))
				{
					float step = Mathf.Clamp01(blendSpeed * Time.deltaTime);
					_ctx.currentWeights[index] = Mathf.Lerp(_ctx.currentWeights[index], target, step);
					if (Mathf.Abs(_ctx.currentWeights[index] - target) < 0.05f)
					{
						_ctx.currentWeights[index] = target;
					}
					targetRenderer.SetBlendShapeWeight(index, _ctx.currentWeights[index]);
				}
			}
		}

		private void ResolveVisemeAndGetIndex(string viseme, out int index, out float shape)
		{
			index = -1;
			shape = 0.0f;

			if (string.IsNullOrEmpty(viseme) || viseme == "sil")
			{
				return;
			}
			
			if (_ctx.blendShapeIndex.TryGetValue(viseme, out int idx))
			{
				index = idx;
				shape = VisemeIntensities.TryGetValue(viseme, out float v) ? v : 0.5f;
			}
		}

		private bool WeightsSettled()
		{
			for (int i = 0; i < _ctx.lipAndJawIndices.Count; i++)
			{
				int lipIndex = _ctx.lipAndJawIndices[i];
				if (_ctx.currentWeights[lipIndex] > 0.5f)
				{
					return false;
				}
			}
			return true;
		}

		private IEnumerator EaseToRest()
		{
			while (!WeightsSettled())
			{
				ApplyTargets("sil");
				yield return null;
			}
		}

		#endregion

		#region procedural motion

		private void UpdateBrow()
		{
			float amp = IsTalking() ? 25.0f : 6.0f;
			float n = Mathf.PerlinNoise(Time.time * 0.5f, 7.3f);
			_ctx.currentWeights[_ctx.browIndex] = n * amp;
			targetRenderer.SetBlendShapeWeight(_ctx.browIndex, _ctx.currentWeights[_ctx.browIndex]);
		}

		private void UpdateBlink()
		{
			const float BlinkCloseDur = 0.08f;
			const float BlinkHoldDur = 0.04f;
			const float BlinkOpenDur = 0.12f;

			if (_ctx.blinkIndices.Count == 0)
			{
				return;
			}

			if (_ctx.blinkPhase < 0.0f && Time.time >= _ctx.nextBlinkTime)
			{
				_ctx.blinkPhase = 0.0f;
				_ctx.extraBlinks = Random.value < doubleBlinkChance ? 1 : 0;
			}

			if (_ctx.blinkPhase < 0.0f)
			{
				return;
			}

			_ctx.blinkPhase += Time.deltaTime;

			float total = BlinkCloseDur + BlinkHoldDur + BlinkOpenDur;

			float weight;
			if (_ctx.blinkPhase < BlinkCloseDur)
			{
				weight = _ctx.blinkPhase / BlinkCloseDur;
			}
			else if (_ctx.blinkPhase < BlinkCloseDur + BlinkHoldDur)
			{
				weight = 1.0f;
			}
			else if (_ctx.blinkPhase < total)
			{
				weight = 1.0f - (_ctx.blinkPhase - BlinkCloseDur - BlinkHoldDur) / BlinkOpenDur;
			}
			else
			{
				weight = 0.0f;
				_ctx.blinkPhase = -1.0f;
				if (_ctx.extraBlinks > 0)
				{
					_ctx.extraBlinks--;
					_ctx.nextBlinkTime = Time.time + 0.12f;
				}
				else
				{
					_ctx.nextBlinkTime = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);
				}
			}

			float w = Mathf.Clamp01(weight) * 100.0f;
			for (int i = 0; i < _ctx.blinkIndices.Count; i++)
			{
				_ctx.currentWeights[_ctx.blinkIndices[i]] = w;
				targetRenderer.SetBlendShapeWeight(_ctx.blinkIndices[i], w);
			}
		}

		private void UpdateHead()
		{
			float targetAmp = IsTalking() ? headTalkAmplitude : headIdleAmplitude;
			float targetBlend = IsTalking() ? 1.0f : 0.0f;
			_ctx.headAmp = Mathf.Lerp(_ctx.headAmp, targetAmp, Mathf.Clamp01(Time.deltaTime * 4.0f));
			_ctx.headTalkBlend = Mathf.Lerp(_ctx.headTalkBlend, targetBlend, Mathf.Clamp01(Time.deltaTime * 4.0f));
			float amp = _ctx.headAmp;
			float tt = Time.time * headFrequency;

			float pitch = (Mathf.PerlinNoise(tt, 0.0f) - 0.5f) * 2.0f * amp;
			float yaw = (Mathf.PerlinNoise(0.0f, tt + 13.7f) - 0.5f) * 2.0f * amp;
			float roll = (Mathf.PerlinNoise(tt + 31.2f, tt) - 0.5f) * 2.0f * amp * 0.5f;

			pitch += Mathf.Sin(Time.time * 1.7f) * amp * 0.25f * _ctx.headTalkBlend;

			headBone.localRotation = _ctx.headRestRot * Quaternion.Euler(pitch, yaw, roll);
		}

		#endregion

		#region utils

		public LipSyncContext GetContext()
		{
			return _ctx;
		}

		#endregion

	}
}

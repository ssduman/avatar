using UnityEngine;
using System.Text;
using System.Collections;
using System.Collections.Generic;

namespace Test2
{
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

	public struct VisemeKey
	{
		public string viseme;
		public float time;

		public VisemeKey(string viseme, float time)
		{
			this.viseme = viseme;
			this.time = time;
		}

		public override string ToString()
		{
			return $"'{viseme}' at {time:F2} sec";
		}
	}

	public class LipSyncManager : MonoBehaviour
	{
		public SkinnedMeshRenderer targetRenderer;

		public TextAsset cmuDictAsset;
		public bool useCMUDict = false;

		public TextAsset transcriptJson;

		[Header("Blend Shapes")]
		[Range(0.0f, 1.0f)] public float jawApplyRatio = 0.1f;
		[Range(0.0f, 1.0f)] public float blendApplyRatio = 0.4f;
		[Range(0.0f, 100.0f)] public float blendSpeed = 28.0f;

		[Header("Brown")]
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

		[Header("Debug")]
		public TMPro.TMP_Text timeText;

		private float[] _currentWeights;
		private Dictionary<string, int> _blendShapeIndex = new Dictionary<string, int>();
		private Dictionary<string, string[]> _cmuDict = new Dictionary<string, string[]>();

		private int _browIndex = -1;
		private List<int> _blinkIndices = new List<int>();

		private Quaternion _headRestRot;
		private bool _hasHeadRest;

		private float _nextBlinkTime;
		private float _blinkPhase = -1.0f;
		private int _pendingBlinks;
		private const float BlinkCloseDur = 0.08f;
		private const float BlinkHoldDur = 0.04f;
		private const float BlinkOpenDur = 0.12f;

		private float _nextSaccadeTime;
		private Vector2 _saccadeCurrent;
		private Vector2 _saccadeTarget;

		private Coroutine _playRoutine;

		private float _headAmp;
		private float _headTalkBlend;

		private float _timer;

		private static readonly HashSet<string> OpenVowels = new HashSet<string>
		{
			"aa", "oh", "ou",
		};
		private static readonly string[] VisemeKeys = new string[]
		{
			"sil", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "aa", "e", "ih", "oh", "ou",
		};

		private static readonly Dictionary<string, float> VisemeIntensity = new Dictionary<string, float>
		{
			{ "aa", 1.0f }, { "oh", 0.95f }, { "ou", 0.85f },
			{ "e", 0.7f }, { "ih", 0.6f }, { "rr", 0.55f },
			{ "th", 0.5f }, { "dd", 0.45f }, { "nn", 0.45f }, { "kk", 0.45f },
			{ "ch", 0.45f }, { "ss", 0.4f }, { "ff", 0.4f }, { "pp", 0.35f },
		};

		private void Start()
		{
			if (targetRenderer == null || targetRenderer.sharedMesh == null)
			{
				Debug.LogError("[LipSync] No SkinnedMeshRenderer with a mesh assigned.", this);
				return;
			}

			_nextBlinkTime = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);

			LoadCMUDict();
			ResolveBlendShapes();
			CacheBoneRests();
		}

		private void Update()
		{
			if (_currentWeights == null)
			{
				return;
			}

			if (enableBlink && _blinkIndices.Count > 0)
			{
				UpdateBlink();
			}
			if (enableHeadMotion && _hasHeadRest)
			{
				UpdateHead();
			}
			if (enableBrowMotion && _browIndex >= 0)
			{
				UpdateBrow();
			}
		}

		#region init

		private void LoadCMUDict()
		{
			if (cmuDictAsset == null)
			{
				Debug.LogWarning("[LipSync] useCMUDict is on but no cmuDictAsset assigned. Using rules.", this);
				return;
			}

			_cmuDict.Clear();

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

				string word = tokens[0];
				int paren = word.IndexOf('(');
				if (paren >= 0)
				{
					word = word.Substring(0, paren);
				}

				if (_cmuDict.ContainsKey(word))
				{
					continue;
				}

				string[] phonemes = new string[tokens.Length - 1];
				System.Array.Copy(tokens, 1, phonemes, 0, phonemes.Length);
				_cmuDict[word] = phonemes;
			}

			Debug.Log($"[LipSync] Loaded {_cmuDict.Count} CMUdict entries. world: {string.Join(" ", _cmuDict["world"])}", this);
		}

		private void ResolveBlendShapes()
		{
			Mesh mesh = targetRenderer.sharedMesh;

			_browIndex = -1;
			_blendShapeIndex.Clear();
			_blinkIndices.Clear();

			_currentWeights = new float[mesh.blendShapeCount];

			for (int i = 0; i < mesh.blendShapeCount; i++)
			{
				_currentWeights[i] = targetRenderer.GetBlendShapeWeight(i);

				string full = mesh.GetBlendShapeName(i);
				string key = ShortName(full);

				foreach (string v in VisemeKeys)
				{
					if (key == v)
					{
						_blendShapeIndex[v] = i;
					}
				}

				if (key.StartsWith("jawopen") && !_blendShapeIndex.ContainsKey("jaw"))
				{
					_blendShapeIndex["jaw"] = i;
				}
				if (key.Contains("blink") || key.Contains("eyesclosed") || key.Contains("eyeclose"))
				{
					_blinkIndices.Add(i);
				}
				if (_browIndex < 0 && key.Contains("brow") && (key.Contains("up") || key.Contains("raise")))
				{
					_browIndex = i;
				}
			}

			if (enableBlink && _blinkIndices.Count == 0)
			{
				Debug.LogWarning("[LipSync] enableBlink is on but no blink blendshape found (looked for 'blink'/'eyesclosed').", this);
			}

			List<string> missing = new List<string>();
			foreach (string v in VisemeKeys)
			{
				if (!_blendShapeIndex.ContainsKey(v))
				{
					missing.Add(v);
				}
			}
			if (missing.Count > 0)
			{
				Debug.LogWarning($"[LipSync] Mesh missing visemes (will be skipped): {string.Join(", ", missing)}", this);
			}

			Debug.Log($"[LipSync] Resolved {_blendShapeIndex.Count} viseme/jaw blendshapes on '{mesh.name}'.", this);
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
				_headRestRot = headBone.localRotation;
				_hasHeadRest = true;
				_headAmp = headIdleAmplitude;
				_headTalkBlend = 0.0f;
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

			_timer = 0.0f;
			timeText.SetText($"time: {_timer:F2}");

			Transcript transcript;
			try
			{
				transcript = JsonUtility.FromJson<Transcript>(transcriptJson.text);
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

			if (_playRoutine != null)
			{
				StopCoroutine(_playRoutine);
			}

			_playRoutine = StartCoroutine(PlayRoutine(schedule));
		}

		public void Stop()
		{
			if (_playRoutine != null)
			{
				StopCoroutine(_playRoutine);
				_playRoutine = null;
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
				_timer += Time.deltaTime;
				timeText.SetText($"time: {_timer:F2}");

				float t = Time.time - startTime;

				while (index < schedule.Count - 1 && schedule[index + 1].time <= t)
				{
					index++;
				}

				VisemeKey cur = schedule[index];
				if (index < schedule.Count - 1)
				{
					VisemeKey next = schedule[index + 1];
					float span = next.time - cur.time;
					float f = span > 0.0001f ? Mathf.Clamp01((t - cur.time) / span) : 1.0f;
					f = f * f * (3.0f - 2.0f * f);
					ApplyTargets(cur.viseme, 1.0f - f, next.viseme, f);
				}
				else
				{
					ApplyTargets(cur.viseme, 1.0f, null, 0.0f);
				}

				if (t >= endTime && WeightsSettled())
				{
					break;
				}

				yield return null;
			}

			_playRoutine = null;
		}

		private bool IsTalking()
		{
			return _playRoutine != null;
		}

		#endregion

		#region shedule

		private List<VisemeKey> BuildSchedule(Transcript transcript)
		{
			List<VisemeKey> keys = new List<VisemeKey>();
			float prevEnd = 0.0f;

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

				List<string> visemes = TextToVisemes(seg.text);
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
						keys.Add(new VisemeKey(visemes[i], start + i * step));
					}
				}

				prevEnd = end;
			}

			keys.Add(new VisemeKey("sil", prevEnd));

			StringBuilder sbVisemes = new StringBuilder();
			foreach (VisemeKey k in keys)
			{
				sbVisemes.Append($" '{k.viseme}' at {k.time:F2} sec |");
			}
			Debug.Log($"[LipSync] text: {transcript}, visemes: {sbVisemes.ToString()}", this);

			return keys;
		}

		#endregion

		#region viseme

		private List<string> TextToVisemes(string text)
		{
			List<string> result = new List<string>();
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

				if (useCMUDict && _cmuDict != null && _cmuDict.TryGetValue(word, out string[] phonemes))
				{
					foreach (string ph in phonemes)
					{
						AppendPhonemeViseme(ph, result);
					}
				}
				else
				{
					AppendWordVisemesByRules(word, result);
				}

				// brief mouth close between words (not after the last word)
				if (w < words.Length - 1)
				{
					AddViseme(result, "sil");
				}
			}

			return result;
		}

		private void AppendPhonemeViseme(string phoneme, List<string> result)
		{
			string p = phoneme.TrimEnd('0', '1', '2');
			switch (p)
			{
				// diphthongs -> two shapes
				case "AW": AddViseme(result, "aa"); AddViseme(result, "ou"); return; // how
				case "AY": AddViseme(result, "aa"); AddViseme(result, "ih"); return; // my
				case "OY": AddViseme(result, "oh"); AddViseme(result, "ih"); return; // boy
				case "EY": AddViseme(result, "e"); AddViseme(result, "ih"); return; // say
				case "OW": AddViseme(result, "oh"); return; // go

				// monophthong vowels
				case "AA": case "AO": case "AE": case "AH": case "AX": AddViseme(result, "aa"); return;
				case "EH": case "ER": AddViseme(result, "e"); return;
				case "IH": case "IY": AddViseme(result, "ih"); return;
				case "UH": case "UW": AddViseme(result, "ou"); return;

				// consonants
				case "P": case "B": case "M": AddViseme(result, "pp"); return;
				case "F": case "V": AddViseme(result, "ff"); return;
				case "TH": case "DH": AddViseme(result, "th"); return;
				case "T": case "D": case "L": case "DX": AddViseme(result, "dd"); return;
				case "N": AddViseme(result, "nn"); return;
				case "NG": AddViseme(result, "nn"); return;
				case "K": case "G": AddViseme(result, "kk"); return;
				case "CH": case "JH": case "SH": case "ZH": AddViseme(result, "ch"); return;
				case "S": case "Z": AddViseme(result, "ss"); return;
				case "R": AddViseme(result, "rr"); return;

				// glides / aspirate -> no viseme
				case "Y": case "W": case "HH": return;

				default: return;
			}
		}

		private void AppendWordVisemesByRules(string word, List<string> result)
		{
			int i = 0;
			while (i < word.Length)
			{
				char c = word[i];
				char next = i + 1 < word.Length ? word[i + 1] : '\0';

				bool isLast = i == word.Length - 1;

				// consonant digraphs (checked first so 'wh'/'qu' win over glide/vowel rules)
				if (c == 't' && next == 'h') { AddViseme(result, "th"); i += 2; continue; }
				if (c == 's' && next == 'h') { AddViseme(result, "ch"); i += 2; continue; }
				if (c == 'c' && next == 'h') { AddViseme(result, "ch"); i += 2; continue; }
				if (c == 'p' && next == 'h') { AddViseme(result, "ff"); i += 2; continue; }
				if (c == 'n' && next == 'g') { AddViseme(result, "nn"); i += 2; continue; }
				if (c == 'c' && next == 'k') { AddViseme(result, "kk"); i += 2; continue; }
				if (c == 'q' && next == 'u') { AddViseme(result, "kk"); AddViseme(result, "ou"); i += 2; continue; }
				if (c == 'w' && next == 'h') { AddViseme(result, "ou"); i += 2; continue; }

				// vowel digraphs / diphthongs (map to the dominant mouth shape)
				if (c == 'o' && next == 'w') { AddViseme(result, "ou"); i += 2; continue; } // how, now
				if (c == 'o' && next == 'u') { AddViseme(result, "ou"); i += 2; continue; } // you, soup
				if (c == 'o' && next == 'o') { AddViseme(result, "ou"); i += 2; continue; } // food, moon
				if (c == 'o' && next == 'a') { AddViseme(result, "oh"); i += 2; continue; } // boat
				if (c == 'o' && (next == 'i' || next == 'y')) { AddViseme(result, "oh"); AddViseme(result, "ih"); i += 2; continue; } // boy, coin
				if (c == 'e' && (next == 'e' || next == 'a' || next == 'i')) { AddViseme(result, "e"); i += 2; continue; } // see, eat, vein
				if (c == 'a' && (next == 'i' || next == 'y')) { AddViseme(result, "e"); i += 2; continue; } // rain, day
				if (c == 'a' && (next == 'u' || next == 'w')) { AddViseme(result, "aa"); i += 2; continue; } // saw, caught
				if (c == 'u' && next == 'e') { AddViseme(result, "ou"); i += 2; continue; } // blue, true
				if (c == 'i' && next == 'e') { AddViseme(result, "ih"); i += 2; continue; } // pie, tie

				// leading glide y/w: consonant onset, no viseme of its own (you, we)
				if (i == 0 && (c == 'y' || c == 'w') && word.Length > 1) { i++; continue; }

				// silent trailing 'e' (are, name, like) - keep short words (be, he)
				if (c == 'e' && isLast && word.Length > 2) { i++; continue; }

				// single letters
				string v = LetterToViseme(c);
				if (!string.IsNullOrEmpty(v))
				{
					AddViseme(result, v);
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

		private void AddViseme(List<string> list, string v)
		{
			if (list.Count > 0 && list[list.Count - 1] == v)
			{
				return;
			}
			list.Add(v);
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
			float dt = Time.deltaTime * blendSpeed;

			int idxA = ResolveViseme(visemeA, out float shapeA);
			int idxB = ResolveViseme(visemeB, out float shapeB);
			shapeA *= weightA;
			shapeB *= weightB;

			float jawWeight = 0.0f;
			if (visemeA != null && OpenVowels.Contains(visemeA))
			{
				jawWeight = Mathf.Max(jawWeight, weightA);
			}

			if (visemeB != null && OpenVowels.Contains(visemeB))
			{
				jawWeight = Mathf.Max(jawWeight, weightB);
			}

			int jawIndex = _blendShapeIndex.TryGetValue("jaw", out int ji) ? ji : -1;
			float jawTarget = jawWeight * jawApplyRatio * 100.0f;

			float scaleA = shapeA * blendApplyRatio * 100.0f;
			float scaleB = shapeB * blendApplyRatio * 100.0f;

			for (int i = 0; i < _currentWeights.Length; i++)
			{
				float target = 0.0f;
				if (i == idxA)
				{
					target = Mathf.Max(target, scaleA);
				}
				if (i == idxB)
				{
					target = Mathf.Max(target, scaleB);
				}
				if (i == jawIndex)
				{
					target = Mathf.Max(target, jawTarget);
				}

				if (!Mathf.Approximately(_currentWeights[i], target))
				{
					float step = Mathf.Clamp01(dt);
					_currentWeights[i] = Mathf.Lerp(_currentWeights[i], target, step);
					if (Mathf.Abs(_currentWeights[i] - target) < 0.05f)
					{
						_currentWeights[i] = target;
					}
					targetRenderer.SetBlendShapeWeight(i, _currentWeights[i]);
				}
			}
		}

		private int ResolveViseme(string viseme, out float shape)
		{
			shape = 0.0f;
			if (string.IsNullOrEmpty(viseme) || viseme == "sil")
			{
				return -1;
			}
			if (_blendShapeIndex.TryGetValue(viseme, out int idx))
			{
				shape = VisemeIntensity.TryGetValue(viseme, out float v) ? v : 0.5f;
				return idx;
			}
			return -1;
		}

		private bool WeightsSettled()
		{
			for (int i = 0; i < _currentWeights.Length; i++)
			{
				if (_currentWeights[i] > 0.5f)
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
			targetRenderer.SetBlendShapeWeight(_browIndex, n * amp);
		}

		private void UpdateBlink()
		{
			if (_blinkIndices.Count == 0)
			{
				return;
			}

			if (_blinkPhase < 0.0f && Time.time >= _nextBlinkTime)
			{
				_blinkPhase = 0.0f;
				_pendingBlinks = Random.value < doubleBlinkChance ? 1 : 0;
			}

			if (_blinkPhase < 0.0f)
			{
				return;
			}

			_blinkPhase += Time.deltaTime;

			float total = BlinkCloseDur + BlinkHoldDur + BlinkOpenDur;

			float weight;
			if (_blinkPhase < BlinkCloseDur)
			{
				weight = _blinkPhase / BlinkCloseDur;
			}
			else if (_blinkPhase < BlinkCloseDur + BlinkHoldDur)
			{
				weight = 1.0f;
			}
			else if (_blinkPhase < total)
			{
				weight = 1.0f - (_blinkPhase - BlinkCloseDur - BlinkHoldDur) / BlinkOpenDur;
			}
			else
			{
				weight = 0.0f;
				_blinkPhase = -1.0f;
				if (_pendingBlinks > 0)
				{
					_pendingBlinks--;
					_nextBlinkTime = Time.time + 0.12f;
				}
				else
				{
					_nextBlinkTime = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);
				}
			}

			float w = Mathf.Clamp01(weight) * 100.0f;
			for (int i = 0; i < _blinkIndices.Count; i++)
			{
				targetRenderer.SetBlendShapeWeight(_blinkIndices[i], w);
			}
		}

		private void UpdateHead()
		{
			float targetAmp = IsTalking() ? headTalkAmplitude : headIdleAmplitude;
			float targetBlend = IsTalking() ? 1.0f : 0.0f;
			_headAmp = Mathf.Lerp(_headAmp, targetAmp, Mathf.Clamp01(Time.deltaTime * 4.0f));
			_headTalkBlend = Mathf.Lerp(_headTalkBlend, targetBlend, Mathf.Clamp01(Time.deltaTime * 4.0f));
			float amp = _headAmp;
			float tt = Time.time * headFrequency;

			float pitch = (Mathf.PerlinNoise(tt, 0.0f) - 0.5f) * 2.0f * amp;
			float yaw = (Mathf.PerlinNoise(0.0f, tt + 13.7f) - 0.5f) * 2.0f * amp;
			float roll = (Mathf.PerlinNoise(tt + 31.2f, tt) - 0.5f) * 2.0f * amp * 0.5f;

			pitch += Mathf.Sin(Time.time * 1.7f) * amp * 0.25f * _headTalkBlend;

			headBone.localRotation = _headRestRot * Quaternion.Euler(pitch, yaw, roll);
		}

		#endregion

	}
}

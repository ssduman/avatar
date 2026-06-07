using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

namespace ls
{
	public class LipSyncDebugUI : MonoBehaviour
	{
		public LipSyncManager manager;

		public bool visible = true;

		private string _jsonText = "";

		private Vector2 _windowScroll;
		private Vector2 _scheduleScroll;
		private Rect _window = new Rect(20, 20, 460, 900);

		private bool _stylesReady;
		private GUIStyle _headerStyle;
		private GUIStyle _barBackStyle;
		private GUIStyle _barFillStyle;
		private GUIStyle _activeRowStyle;
		private GUIStyle _activeWordStyle;

		private Texture2D _texGray;
		private Texture2D _texGreen;
		private Texture2D _texHighlight;

		private void Start()
		{
			if (manager != null && manager.transcriptJson != null)
			{
				_jsonText = manager.transcriptJson.text;
			}
		}

		private void Update()
		{
			if (Keyboard.current.f1Key.wasPressedThisFrame)
			{
				visible = !visible;
			}
		}

		private void OnGUI()
		{
			if (!visible || manager == null)
			{
				return;
			}

			EnsureStyles();

			_window = GUILayout.Window(GetInstanceID(), _window, DrawWindow, "LipSync — F1 to hide");
		}

		#region drawing

		private void DrawWindow(int id)
		{
			_windowScroll = GUILayout.BeginScrollView(_windowScroll);

			DrawMain();
			GUILayout.Space(6);
			DrawNowPlaying();
			GUILayout.Space(6);
			DrawVisemeWeights();
			GUILayout.Space(6);
			DrawWords();
			GUILayout.Space(6);
			DrawSchedule();

			GUILayout.EndScrollView();
			GUI.DragWindow(new Rect(0, 0, 10000, 20));
		}

		private void DrawMain()
		{
			GUILayout.Label("Main", _headerStyle);

			_jsonText = GUILayout.TextArea(_jsonText, GUILayout.MinHeight(80));

			manager.useCMUDict = Toggle(manager.useCMUDict, " use CMU dictionary");
			manager.jawApplyRatio = Slider("jaw", manager.jawApplyRatio, 0f, 1f);
			manager.blendApplyRatio = Slider("blend", manager.blendApplyRatio, 0f, 1f);
			manager.blendSpeed = Slider("blendSpeed", manager.blendSpeed, 0f, 100f);

			GUILayout.BeginHorizontal();
			if (GUILayout.Button("Play", GUILayout.Height(28)))
			{
				manager.Play(_jsonText);
			}
			if (GUILayout.Button("Stop", GUILayout.Height(28)))
			{
				manager.Stop();
			}
			GUILayout.EndHorizontal();
		}

		private void DrawNowPlaying()
		{
			GUILayout.Label("Now Playing", _headerStyle);

			LipSyncContext ctx = manager.GetContext();

			bool playing = ctx.playRoutine != null;

			GUILayout.Label($"state: {(playing ? "PLAYING" : "idle")}    time: {ctx.timer:F2} / {ctx.scheduleEnd:F2}");
			GUILayout.Label($"word: '{ctx.currentWord}'  #{ctx.currentWordIndex}");
			GUILayout.Label($"phoneme: {PrettyStr(ctx.currentPhoneme)}");
			GUILayout.Label($"viseme: {PrettyStr(ctx.currentViseme)} -> {PrettyStr(ctx.nextViseme)}   f={ctx.blendFactor:F2}");
		}

		private void DrawVisemeWeights()
		{
			GUILayout.Label("Viseme Weights", _headerStyle);

			LipSyncContext ctx = manager.GetContext();

			Dictionary<string, int> map = ctx.blendShapeIndex;
			if (map == null || map.Count == 0)
			{
				GUILayout.Label("(no blendshapes resolved — enter Play mode)");
				return;
			}

			float[] weights = ctx.currentWeights;
			foreach (KeyValuePair<string, int> kv in map)
			{
				float w = (weights != null && kv.Value >= 0 && kv.Value < weights.Length) ? weights[kv.Value] : 0.0f;
				Bar(kv.Key, w / 100.0f, $"{w:F0}");
			}
		}

		private void DrawWords()
		{
			GUILayout.Label("Words", _headerStyle);

			LipSyncContext ctx = manager.GetContext();

			List<string> words = ctx.words;
			if (words == null || words.Count == 0)
			{
				GUILayout.Label("(none)");
				return;
			}

			int active = ctx.currentWordIndex;
			GUILayout.BeginHorizontal();
			float lineWidth = 0f;
			for (int i = 0; i < words.Count; i++)
			{
				GUIStyle style = i == active ? _activeWordStyle : GUI.skin.label;
				GUIContent content = new GUIContent(words[i] + " ");
				float w = style.CalcSize(content).x;

				if (lineWidth + w > _window.width - 40f)
				{
					GUILayout.EndHorizontal();
					GUILayout.BeginHorizontal();
					lineWidth = 0f;
				}

				GUILayout.Label(content, style, GUILayout.Width(w));
				lineWidth += w;
			}
			GUILayout.EndHorizontal();
		}

		private void DrawSchedule()
		{
			GUILayout.Label("Schedule", _headerStyle);

			LipSyncContext ctx = manager.GetContext();

			List<VisemeKey> schedule = ctx.schedule;
			if (schedule == null || schedule.Count == 0)
			{
				GUILayout.Label("(empty — press Play)");
				return;
			}

			int cur = ctx.scheduleIndex;
			_scheduleScroll = GUILayout.BeginScrollView(_scheduleScroll, GUILayout.Height(200));
			for (int i = 0; i < schedule.Count; i++)
			{
				VisemeKey k = schedule[i];
				string marker = i == cur ? "> " : "  ";
				string line = $"{marker}{k.time,6:F2}  {k.viseme,-4} '{k.word}'";
				if (i == cur)
				{
					GUILayout.Label(line, _activeRowStyle);
				}
				else
				{
					GUILayout.Label(line);
				}
			}
			GUILayout.EndScrollView();
		}

		#endregion

		#region widgets

		private bool Toggle(bool value, string label)
		{
			return GUILayout.Toggle(value, label);
		}

		private void Bar(string label, float fill01, string valueText)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(40));

			Rect r = GUILayoutUtility.GetRect(180, 16, GUILayout.ExpandWidth(true));
			GUI.Box(r, GUIContent.none, _barBackStyle);
			Rect fillRect = new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill01), r.height);
			GUI.Box(fillRect, GUIContent.none, _barFillStyle);

			GUILayout.Label(valueText, GUILayout.Width(36));
			GUILayout.EndHorizontal();
		}

		private float Slider(string label, float value, float min, float max)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label($"{label}: {value:F2}", GUILayout.Width(130));
			value = GUILayout.HorizontalSlider(value, min, max);
			GUILayout.EndHorizontal();
			return value;
		}

		#endregion

		#region style

		private void EnsureStyles()
		{
			if (_stylesReady)
			{
				return;
			}

			_texGray = SolidTex(new Color(0.2f, 0.2f, 0.2f, 0.8f));
			_texGreen = SolidTex(new Color(0.3f, 0.8f, 0.4f, 0.9f));
			_texHighlight = SolidTex(new Color(0.9f, 0.7f, 0.2f, 0.35f));

			_barBackStyle = new GUIStyle(GUI.skin.box);
			_barBackStyle.normal.background = _texGray;
			_barBackStyle.margin = new RectOffset(0, 0, 0, 0);

			_barFillStyle = new GUIStyle(GUI.skin.box);
			_barFillStyle.normal.background = _texGreen;

			_headerStyle = new GUIStyle(GUI.skin.label);
			_headerStyle.fontStyle = FontStyle.Bold;

			_activeWordStyle = new GUIStyle(GUI.skin.label);
			_activeWordStyle.fontStyle = FontStyle.Bold;
			_activeWordStyle.normal.textColor = new Color(1.0f, 0.85f, 0.3f);

			_activeRowStyle = new GUIStyle(GUI.skin.label);
			_activeRowStyle.fontStyle = FontStyle.Bold;
			_activeRowStyle.normal.textColor = new Color(1.0f, 0.85f, 0.3f);
			_activeRowStyle.normal.background = _texHighlight;

			_stylesReady = true;
		}

		#endregion

		#region utils

		private string PrettyStr(string s)
		{
			return string.IsNullOrEmpty(s) ? "-" : s;
		}

		private static Texture2D SolidTex(Color c)
		{
			Texture2D tex = new Texture2D(1, 1);
			tex.SetPixel(0, 0, c);
			tex.Apply();
			return tex;
		}

		#endregion
	}
}

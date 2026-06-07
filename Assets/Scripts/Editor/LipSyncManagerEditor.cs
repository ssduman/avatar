using UnityEditor;
using UnityEngine;

namespace ls
{
	[CustomEditor(typeof(LipSyncManager))]
	public class LipSyncManagerEditor : Editor
	{
		public override void OnInspectorGUI()
		{
			DrawDefaultInspector();

			if (!Application.isPlaying)
			{
				return;
			}

			LipSyncManager manager = (LipSyncManager)target;

			EditorGUILayout.Space();

			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button("Play"))
			{
				manager.Play();
			}
			if (GUILayout.Button("Stop"))
			{
				manager.Stop();
			}
			EditorGUILayout.EndHorizontal();
		}
	}
}

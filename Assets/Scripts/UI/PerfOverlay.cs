using LeGamez.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;
using UnityEngine.UI;

namespace LeGamez.UI
{
    /// <summary>
    /// Small performance readout for testing on devices: FPS (smoothed + worst frame), loaded tiles, instanced batches
    /// submitted/culled per frame and allocated memory. Toggle with a three-finger tap (F3 in the editor); hidden by default.
    /// </summary>
    public class PerfOverlay : MonoBehaviour
    {
        Text label;
        float smoothed = 1f / 60f, worst, nextText;

        public static PerfOverlay Create(Transform canvasRoot)
        {
            var rt = UiKit.Rect("Perf", canvasRoot, new Vector2(0f, 0f), new Vector2(20, 120), new Vector2(560, 190));
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            bg.raycastTarget = false;
            var o = rt.gameObject.AddComponent<PerfOverlay>();
            o.label = UiKit.Label(rt, "", 26, Color.white, TextAnchor.UpperLeft);
            UiKit.Stretch(o.label.rectTransform, 10f);
            rt.gameObject.SetActive(false);
            var toggler = new GameObject("PerfToggle").AddComponent<Toggler>();
            toggler.target = rt.gameObject;
            toggler.transform.SetParent(canvasRoot, false);
            return o;
        }

        /// <summary>Lives outside the (hidden) overlay so it keeps listening.</summary>
        class Toggler : MonoBehaviour
        {
            public GameObject target;
            bool wasThree;

            void Update()
            {
                bool toggle = false;
                var ts = Touchscreen.current;
                if (ts != null)
                {
                    int down = 0;
                    foreach (var t in ts.touches) if (t.press.isPressed) down++;
                    if (down >= 3 && !wasThree) toggle = true;
                    wasThree = down >= 3;
                }
                var kb = Keyboard.current;
                if (kb != null && kb.f3Key.wasPressedThisFrame) toggle = true;
                if (toggle && target != null) target.SetActive(!target.activeSelf);
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            smoothed = Mathf.Lerp(smoothed, dt, 0.05f);
            worst = Mathf.Max(worst, dt);
            if (Time.unscaledTime < nextText) return;
            nextText = Time.unscaledTime + 0.5f;
            var w = WorldStreamer.Instance;
            long mb = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            label.text = $"{1f / smoothed:0} fps · schlechtester Frame {worst * 1000f:0} ms\n" +
                         $"Kacheln {(w != null ? w.LoadedCount : 0)} · Instanz-Batches {InstancedProps.Submitted} gezeichnet / {InstancedProps.Culled} verworfen\n" +
                         $"Speicher {mb} MB";
            worst = 0f;
        }
    }
}

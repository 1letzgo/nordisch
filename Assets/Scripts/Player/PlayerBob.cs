using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Subtle vertical walk bob + forward lean for the placeholder figure (no side roll).</summary>
    public class PlayerBob : MonoBehaviour
    {
        PlayerController player;
        Vector3 basePos;
        float phase;
        float lean;

        void Start()
        {
            player = GetComponentInParent<PlayerController>();
            basePos = transform.localPosition;
        }

        void Update()
        {
            float speed = player != null ? player.CurrentSpeed : 0f;
            phase += speed * Time.deltaTime * 2.6f;
            float amp = Mathf.Clamp01(speed / 6f) * 0.035f;
            transform.localPosition = basePos + Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * amp;
            lean = Mathf.Lerp(lean, Mathf.Clamp(speed * 1.2f, 0f, 7f), 1f - Mathf.Exp(-6f * Time.deltaTime));
            transform.localRotation = Quaternion.Euler(lean, 0f, 0f);
        }
    }
}

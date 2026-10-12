using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>Cheap walk bob + lean for the placeholder figure (replaced by real animation later).</summary>
    public class PlayerBob : MonoBehaviour
    {
        PlayerController player;
        Vector3 basePos;
        float phase;

        void Start()
        {
            player = GetComponentInParent<PlayerController>();
            basePos = transform.localPosition;
        }

        void Update()
        {
            float speed = player != null ? player.CurrentSpeed : 0f;
            phase += speed * Time.deltaTime * 3.2f;
            float amp = Mathf.Clamp01(speed / 6f) * 0.06f;
            transform.localPosition = basePos + Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * amp;
            transform.localRotation = Quaternion.Euler(Mathf.Clamp(speed * 2f, 0f, 12f), 0f, Mathf.Sin(phase) * amp * 40f);
        }
    }
}

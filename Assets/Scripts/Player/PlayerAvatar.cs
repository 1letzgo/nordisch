using LeGamez.Core;
using UnityEngine;

namespace LeGamez.Player
{
    /// <summary>
    /// Spawns the selected Kenney character under the player and drives its Animator
    /// from the PlayerController (speed, grounded, jump).
    /// </summary>
    public class PlayerAvatar : MonoBehaviour
    {
        const string PrefKey = "legamez.character";

        public static int SelectedIndex
        {
            get => PlayerPrefs.GetInt(PrefKey, 0);
            set { PlayerPrefs.SetInt(PrefKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>Human-readable name for the menu, e.g. "character-female-b" → "Figur 8".</summary>
        public static string DisplayName(int index) => $"Figur {index + 1}";

        Animator animator;
        PlayerController player;
        float groundedTimer;

        /// <summary>Returns false if no character assets are available (caller keeps the placeholder).</summary>
        public static bool TryCreate(GameObject playerRoot)
        {
            var gm = GameMaterials.Instance;
            if (gm == null || gm.characters == null || gm.characters.Count == 0 || gm.characterController == null) return false;
            int index = Mathf.Clamp(SelectedIndex, 0, gm.characters.Count - 1);

            var model = Instantiate(gm.characters[index], playerRoot.transform, false);
            model.name = "Avatar";
            // The CharacterController hovers by its skin width; sink the model so feet touch the ground.
            float skin = playerRoot.TryGetComponent<CharacterController>(out var cc) ? cc.skinWidth : 0f;
            model.transform.localPosition = Vector3.up * (gm.characterFootOffset - skin);
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one * gm.characterScale;
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = playerRoot.layer;
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = gm.characterMaterial;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            var anim = model.GetComponent<Animator>();
            if (anim == null) anim = model.AddComponent<Animator>();
            anim.runtimeAnimatorController = gm.characterController;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.SetBool("Grounded", true); // parameter defaults to false → would start in "Fall"

            var avatar = playerRoot.AddComponent<PlayerAvatar>();
            avatar.animator = anim;
            return true;
        }

        void Start()
        {
            player = GetComponent<PlayerController>();
            if (player != null) player.Jumped += () => animator.SetTrigger("Jump");
        }

        void Update()
        {
            if (animator == null || player == null) return;
            animator.SetFloat("Speed", player.CurrentSpeed, 0.08f, Time.deltaTime);

            // CharacterController.isGrounded flickers on slopes/steps – debounce before showing "fall".
            bool grounded = player.IsGrounded;
            groundedTimer = grounded ? 0f : groundedTimer + Time.deltaTime;
            bool showGrounded = grounded || groundedTimer < 0.2f;
            animator.SetBool("Grounded", showGrounded);
        }

        /// <summary>0 = on foot, 1 = driving, 2 = sitting (vehicles).</summary>
        public void SetMode(int mode) => animator.SetInteger("Mode", mode);
    }
}

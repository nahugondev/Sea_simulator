using UnityEngine;
using UnityEngine.InputSystem;

namespace SeaSim.CameraControl
{
    /// <summary>
    /// Simple free-fly camera for exploring the simulation.
    /// Hold right mouse button to look around; WASD/ZQSD (physical key positions) to move,
    /// E/Space up, Q/Ctrl down (physical positions), Shift to go faster, mouse wheel to change speed.
    /// </summary>
    public class FlyCamera : MonoBehaviour
    {
        [SerializeField] float moveSpeed = 6f;
        [SerializeField] float boostMultiplier = 3f;
        [SerializeField] float lookSensitivity = 0.12f;
        [SerializeField] float smoothing = 10f;

        Vector3 velocity;
        float yaw, pitch;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
                yaw += d.x;
                pitch = Mathf.Clamp(pitch - d.y, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                moveSpeed = Mathf.Clamp(moveSpeed * (scroll > 0 ? 1.15f : 1f / 1.15f), 0.5f, 100f);

            // Key bindings use physical positions, so WASD on QWERTY == ZQSD on AZERTY.
            Vector3 input = Vector3.zero;
            if (kb.wKey.isPressed) input.z += 1;
            if (kb.sKey.isPressed) input.z -= 1;
            if (kb.dKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed) input.x -= 1;
            if (kb.eKey.isPressed || kb.spaceKey.isPressed) input.y += 1;
            if (kb.qKey.isPressed || kb.leftCtrlKey.isPressed) input.y -= 1;

            float speed = moveSpeed * (kb.leftShiftKey.isPressed ? boostMultiplier : 1f);
            Vector3 target = transform.TransformDirection(input.normalized) * speed;
            velocity = Vector3.Lerp(velocity, target, 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime));
            transform.position += velocity * Time.unscaledDeltaTime;
        }
    }
}

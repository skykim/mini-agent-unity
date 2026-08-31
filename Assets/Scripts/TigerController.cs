using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class TigerController : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Movement speed in world units per second.")]
    [SerializeField] private float _moveSpeed = 6f;

    [Tooltip("Rotation speed in degrees per second.")]
    [SerializeField] private float _turnSpeed = 720f;

    [Tooltip("Gravity acceleration (should be negative, e.g. -20).")]
    [SerializeField] private float _gravity = -20f;

    [Header("References")]
    [Tooltip("The camera transform used to compute camera-relative movement. Assign the main camera at runtime.")]
    [SerializeField] private Transform _cameraTransform;

    [Tooltip("The Animator component on the tiger model.")]
    [SerializeField] private Animator _animator;

    private CharacterController _cc;
    private InputAction _moveAction;
    private float _vy;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();

        // Build movement action inline — no .inputactions asset dependency
        _moveAction = new InputAction("Move", InputActionType.Value);

        // WASD bindings
        _moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")
            .With("Down",  "<Keyboard>/s")
            .With("Left",  "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        // Arrow key bindings
        _moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/upArrow")
            .With("Down",  "<Keyboard>/downArrow")
            .With("Left",  "<Keyboard>/leftArrow")
            .With("Right", "<Keyboard>/rightArrow");
    }

    private void OnEnable()
    {
        _moveAction?.Enable();
    }

    private void OnDisable()
    {
        _moveAction?.Disable();
    }

    private void OnDestroy()
    {
        _moveAction?.Dispose();
    }

    private void Update()
    {
        Vector2 input = _moveAction.ReadValue<Vector2>();

        // Compute camera-relative flat axes
        Vector3 camForward, camRight;
        if (_cameraTransform != null)
        {
            camForward = Vector3.ProjectOnPlane(_cameraTransform.forward, Vector3.up).normalized;
            camRight   = Vector3.ProjectOnPlane(_cameraTransform.right,   Vector3.up).normalized;
        }
        else
        {
            camForward = Vector3.forward;
            camRight   = Vector3.right;
        }

        Vector3 move = camForward * input.y + camRight * input.x;
        if (move.magnitude > 1f)
            move.Normalize();

        // Gravity — keep small negative when grounded to maintain isGrounded contact
        if (_cc.isGrounded)
            _vy = -1f;
        else
            _vy += _gravity * Time.deltaTime;

        // Move the character
        Vector3 velocity = move * _moveSpeed + Vector3.up * _vy;
        _cc.Move(velocity * Time.deltaTime);

        // Rotate smoothly toward movement direction
        if (move.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(move, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, targetRotation, _turnSpeed * Time.deltaTime);
        }

        // Drive blend tree
        if (_animator != null)
            _animator.SetFloat("Speed", move.magnitude);
    }
}

using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

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

    [Tooltip("Upward launch velocity for a jump. With gravity -20, ~8 gives roughly a 1.6-unit hop.")]
    [SerializeField] private float _jumpForce = 8f;

    [Tooltip("Grace period after leaving the ground during which a jump still fires (fixes CharacterController.isGrounded flicker).")]
    [SerializeField] private float _coyoteTime = 0.12f;

    [Tooltip("How long a Space press is remembered so a jump lands even if pressed a hair before touching down.")]
    [SerializeField] private float _jumpBuffer = 0.12f;

    [Header("References")]
    [Tooltip("The camera transform used to compute camera-relative movement. Assign the main camera at runtime.")]
    [SerializeField] private Transform _cameraTransform;

    [Tooltip("The Animator component on the tiger model.")]
    [SerializeField] private Animator _animator;

    private CharacterController _cc;
    private NavMeshAgent _navAgent;
    private InputAction _moveAction;
    private InputAction _jumpAction;
    private float _vy;
    private float _coyoteTimer;      // >0 while a jump is still allowed after leaving ground
    private float _jumpBufferTimer;  // >0 while a recent Space press is still pending

    // ---- programmatic autopilot (agent-driven walk-to-target, NavMesh-routed) ----
    private bool _autoPilot;
    private const float ArriveDistance = 1.8f;   // stop this far from the final target (relaxed so Tiger doesn't crowd the device)
    private const float StuckTime = 0.3f;         // give up (treat as arrived) after this long with no progress
    private Vector3 _autoLastPos;                  // position last autopilot frame (for stuck detection)
    private float _autoStuck;                      // accumulated time making no progress

    /// <summary>True while the agent is walking Tiger to a commanded target.</summary>
    public bool IsAutoMoving => _autoPilot;

    /// <summary>Drive Tiger to a world position, routed around walls by a NavMeshAgent
    /// (steering only — the CharacterController does the actual moving). WASD cancels it.</summary>
    public void MoveTo(Vector3 worldTarget)
    {
        _autoPilot = true;
        _autoStuck = 0f;
        _autoLastPos = transform.position;
        if (_navAgent == null || !_navAgent.isActiveAndEnabled) { _autoPilot = false; return; }

        // If we've been knocked off the NavMesh (e.g. shoved onto furniture by the
        // patrolling vacuum, or repositioned), snap back on first — otherwise the agent
        // can't build a path and the walk just times out with Tiger standing still.
        if (!_navAgent.isOnNavMesh &&
            NavMesh.SamplePosition(transform.position, out var self, 6f, NavMesh.AllAreas))
        {
            transform.position = self.position;
            _navAgent.Warp(self.position);
        }

        _navAgent.nextPosition = transform.position;                 // keep agent synced to us
        if (NavMesh.SamplePosition(worldTarget, out var hit, 8f, NavMesh.AllAreas))
            _navAgent.SetDestination(hit.position);
        else
            _navAgent.SetDestination(worldTarget);
        _navAgent.isStopped = false;
    }

    /// <summary>Hand control back to the player immediately.</summary>
    public void CancelMove()
    {
        _autoPilot = false;
        if (_navAgent != null && _navAgent.isActiveAndEnabled && _navAgent.isOnNavMesh)
            _navAgent.ResetPath();
    }

    /// <summary>Steering vector from the NavMeshAgent (never points into a wall).</summary>
    private Vector3 AutoMove()
    {
        if (_navAgent == null || !_navAgent.isActiveAndEnabled || !_navAgent.isOnNavMesh)
        { _autoPilot = false; return Vector3.zero; }

        if (!_navAgent.pathPending)
        {
            // arrived
            if (_navAgent.remainingDistance <= ArriveDistance) { _autoPilot = false; return Vector3.zero; }
            // no route to the target (off-mesh target, blocked) — bail fast instead of
            // steering nowhere until the walk timeout.
            if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid) { _autoPilot = false; return Vector3.zero; }
        }

        // Stuck detection, but ONLY near the final target: if we're basically at the
        // device yet can't close the last bit (blocked by the device's own collider —
        // e.g. the robot vacuum Tiger can't get within ArriveDistance of), treat it as
        // arrived instead of running in place until the walk timeout. Gated by proximity
        // so a brief snag mid-path (a doorway/furniture edge) never aborts the walk.
        Vector3 v = _navAgent.desiredVelocity; v.y = 0f;
        bool wantsToMove = v.sqrMagnitude > 0.04f;
        float moved = (transform.position - _autoLastPos).magnitude;
        _autoLastPos = transform.position;
        _autoStuck = (wantsToMove && moved < 0.02f) ? _autoStuck + Time.deltaTime : 0f;
        bool nearTarget = _navAgent.remainingDistance <= ArriveDistance + 2f;
        if (_autoStuck >= StuckTime && nearTarget) { _autoPilot = false; _autoStuck = 0f; return Vector3.zero; }

        return Vector3.ClampMagnitude(v / Mathf.Max(0.01f, _moveSpeed), 1f);
    }

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _navAgent = GetComponent<NavMeshAgent>();
        if (_navAgent != null)
        {
            // steering only: the agent plans/steers on the NavMesh, the CharacterController moves
            _navAgent.updatePosition = false;
            _navAgent.updateRotation = false;
            _navAgent.speed = _moveSpeed;
        }

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

        // Jump on Space
        _jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
    }

    private void OnEnable()
    {
        _moveAction?.Enable();
        _jumpAction?.Enable();
    }

    private void OnDisable()
    {
        _moveAction?.Disable();
        _jumpAction?.Disable();
    }

    private void OnDestroy()
    {
        _moveAction?.Dispose();
        _jumpAction?.Dispose();
    }

    private void Update()
    {
        Vector3 move;
        // ignore WASD/arrows while the user is typing in a focused UGUI InputField
        Vector2 input = IsTypingInUI() ? Vector2.zero : _moveAction.ReadValue<Vector2>();

        if (_autoPilot)
        {
            // agent autopilot follows the NavMesh path; a WASD nudge cancels and takes over
            if (input.sqrMagnitude > 0.01f) { CancelMove(); move = InputMove(input); }
            else move = AutoMove();
        }
        else
        {
            move = InputMove(input);
        }

        // Jump (Space). Use coyote-time + input-buffering so a press isn't lost to a
        // one-frame isGrounded flicker (common while moving) or pressing just before landing.
        bool grounded = _cc.isGrounded;
        _coyoteTimer = grounded ? _coyoteTime : _coyoteTimer - Time.deltaTime;

        bool jumpPressed = !IsTypingInUI() && _jumpAction != null && _jumpAction.WasPressedThisFrame();
        _jumpBufferTimer = jumpPressed ? _jumpBuffer : _jumpBufferTimer - Time.deltaTime;

        // Gravity — keep a small negative while grounded to maintain contact, else fall.
        if (grounded && _vy < 0f)
            _vy = -1f;
        else
            _vy += _gravity * Time.deltaTime;

        // Fire a buffered jump if we're grounded (or within the coyote window).
        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
        {
            _vy = _jumpForce;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
        }

        // Move the character
        Vector3 velocity = move * _moveSpeed + Vector3.up * _vy;
        _cc.Move(velocity * Time.deltaTime);

        // keep the (position-decoupled) NavMeshAgent synced to where the CC actually is
        if (_navAgent != null && _navAgent.isActiveAndEnabled && _navAgent.isOnNavMesh)
            _navAgent.nextPosition = transform.position;

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

    /// <summary>True while a UGUI InputField currently has keyboard focus.</summary>
    private static bool IsTypingInUI()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        var field = sel.GetComponent<InputField>();
        return field != null && field.isFocused;
    }

    /// <summary>Camera-relative move vector from WASD/arrow input (magnitude ≤ 1).</summary>
    private Vector3 InputMove(Vector2 input)
    {
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
        return move.magnitude > 1f ? move.normalized : move;
    }
}

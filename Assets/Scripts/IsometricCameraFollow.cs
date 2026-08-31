using UnityEngine;

/// <summary>
/// Isometric camera follow: smoothly tracks a target with a fixed offset,
/// then orients toward the target each LateUpdate.
/// </summary>
public class IsometricCameraFollow : MonoBehaviour
{
    [Header("Follow Settings")]
    [Tooltip("The transform to follow (e.g. the Tiger prefab root).")]
    [SerializeField] private Transform _target;

    [Tooltip("World-space offset from the target position.")]
    [SerializeField] private Vector3 _offset = new Vector3(-25f, 30f, -25f);

    [Tooltip("Lerp smoothing factor. Higher = snappier.")]
    [SerializeField] private float _smooth = 8f;

    private void Start()
    {
        SnapToTarget();
    }

    /// <summary>
    /// Snaps camera immediately to the desired position and look rotation without lerping.
    /// </summary>
    public void SnapToTarget()
    {
        if (_target == null) return;

        transform.position = _target.position + _offset;
        transform.LookAt(_target.position);
    }

    private void LateUpdate()
    {
        if (_target == null) return;

        Vector3 desired = _target.position + _offset;
        transform.position = Vector3.Lerp(transform.position, desired, _smooth * Time.deltaTime);
        transform.LookAt(_target.position);
    }
}


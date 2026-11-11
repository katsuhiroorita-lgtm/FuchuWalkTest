using UnityEngine;

/// <summary>Animates a struck resident or car, then removes it using gameplay time.</summary>
public sealed class FuchuKnockback : MonoBehaviour
{
    private Vector3 velocity;
    private Vector3 spin;
    private Vector3 up;
    private float elapsed;

    internal static void Launch(Transform target, Transform player)
    {
        var motion = target.gameObject.AddComponent<FuchuKnockback>();
        motion.up = player.up;
        Vector3 direction = Vector3.ProjectOnPlane(target.position - player.position, motion.up);
        if (direction.sqrMagnitude < 0.01f) direction = player.forward;
        motion.velocity = direction.normalized * 12f + motion.up * 9f;
        motion.spin = new Vector3(220f, 130f, 170f);
    }

    private void Update()
    {
        float deltaTime = Time.deltaTime;
        if (deltaTime <= 0f) return;
        velocity -= up * (14f * deltaTime);
        transform.position += velocity * deltaTime;
        transform.Rotate(spin * deltaTime, Space.Self);
        elapsed += deltaTime;
        if (elapsed >= 1.2f) Destroy(gameObject);
    }
}

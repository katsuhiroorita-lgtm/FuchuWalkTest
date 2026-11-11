using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Third-person movement and camera for the local Fuchu City prototype.
/// Attach this to a GameObject with a CharacterController and assign its camera.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public sealed class FuchuWalkController : MonoBehaviour
{
    private const double RunDurationSeconds = 5.0 * 60.0;
    private const double BaseStamina = 100.0;
    private const float SprintStaminaPerSecond = 20f;
    private const double PointsPerSprintBonus = 1000.0;
    private const float MaximumSprintBonus = 50f;
    private const float SprintBonusPerSecond = 5f;

    private const float DefaultCameraPitch = 22f;
    private const float MinCameraPitch = -55f;
    private const float MaxCameraPitch = 60f;
    // Leave room below the player's feet for the trail even when looking straight ahead.
    private const float CameraDistance = 6.2f;
    private const float CameraClearanceAbovePlayer = 0.6f;
    private const float CameraCollisionRadius = 0.16f;

    // Local XZ positions on SampleScene's 1 km road mesh.
    private static readonly Vector2[] SpawnPoints =
    {
        new Vector2(462.352f, -526.300f),
        new Vector2(596.404f, -117.915f),
        new Vector2(179.578f, -133.355f),
        new Vector2(-165.796f, 107.811f),
        new Vector2(-300.892f, -259.761f),
        new Vector2(-638.146f, -511.839f),
        new Vector2(-281.208f, -790.964f),
        new Vector2(-159.809f, -1180.894f),
        new Vector2(217.970f, -874.823f),
        new Vector2(582.587f, -869.466f)
    };

    [SerializeField] private Transform cameraTransform;
    [SerializeField] private float walkSpeed = 4.5f;
    [SerializeField] private float sprintSpeed = 7f;
    [SerializeField] private float lookSensitivity = 0.12f;
    [SerializeField] private float jumpHeight = 1.1f;
    [SerializeField] private float gravity = -20f;

    private CharacterController characterController;
    private FuchuWalkHud walkHud;
    private FuchuWalkingTerritory territory;
    private float verticalSpeed;
    private float cameraPitch;
    private Vector3 startingPosition;
    private Quaternion startingRotation;
    private MeshFilter playerMesh;
    private MeshRenderer playerRenderer;
    private float playerFootHeight;
    private float stepPhase;
    private float sprintBonus;
    private float slowedUntil;
    private double runDeadline;
    private double? pausedRunSeconds;
    private float? timeScaleBeforeMenu;
    private bool runStarted;
    private static bool startAfterReload;

    public double Stamina { get; private set; } = BaseStamina;
    public double MaxStamina => BaseStamina + BuildingPoints;
    public double BuildingPoints { get; private set; }
    public int ToughnessLevel
    {
        get
        {
            if (BuildingPoints >= 1000000.0) return 6;
            if (BuildingPoints >= 500000.0) return 5;
            if (BuildingPoints >= 100000.0) return 4;
            if (BuildingPoints >= 50000.0) return 3;
            if (BuildingPoints >= 10000.0) return 2;
            return 1;
        }
    }
    public double TerritoryCoverage => territory != null ? territory.Coverage : 0.0;
    internal bool RunFinished { get; private set; }
    internal bool IsPlaying => runStarted && !RunFinished && !IsPaused;
    internal bool IsPaused => pausedRunSeconds.HasValue;
    internal bool ReloadPending { get; private set; }
    internal int RunRank { get; private set; }
    internal IReadOnlyList<FuchuRunRanking.Entry> Ranking { get; private set; }
    internal double RemainingRunSeconds => !runStarted || RunFinished ? 0.0 :
        pausedRunSeconds ?? System.Math.Max(0.0, runDeadline - Time.realtimeSinceStartupAsDouble);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetReloadState() => startAfterReload = false;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        walkHud = GetComponent<FuchuWalkHud>();
        startingPosition = transform.localPosition;
        startingRotation = transform.localRotation;
        cameraPitch = DefaultCameraPitch;

        playerFootHeight = characterController.center.y - characterController.height * 0.5f;
        var visual = new GameObject("Player Visual", typeof(MeshFilter), typeof(MeshRenderer));
        visual.transform.SetParent(transform, false);
        visual.transform.localPosition = Vector3.up * playerFootHeight;
        playerMesh = visual.GetComponent<MeshFilter>();
        playerRenderer = visual.GetComponent<MeshRenderer>();
        FuchuResidentSpawner.BuildPlayerVisual(playerMesh, playerRenderer);
    }

    private void Start()
    {
        ChooseStartingPosition();
        var territoryObject = new GameObject("Walking Territory");
        territoryObject.transform.SetParent(transform.parent, false);
        territory = territoryObject.AddComponent<FuchuWalkingTerritory>();
        territory.Initialize(transform.position);
        var recoveryItems = new GameObject("Stamina Recovery Items");
        recoveryItems.transform.SetParent(transform.parent, false);
        recoveryItems.AddComponent<FuchuStaminaItems>().Initialize(this, characterController);
        UpdateCamera();
        bool startImmediately = startAfterReload;
        startAfterReload = false;
        if (startImmediately || walkHud == null || !walkHud.isActiveAndEnabled)
            BeginRun();
        else
            PauseForMenu();
    }

    internal void BeginRun()
    {
        if (runStarted) return;
        runStarted = true;
        ResumeFromMenu();
        runDeadline = Time.realtimeSinceStartupAsDouble + RunDurationSeconds;
        LockCursor();
    }

    private void ChooseStartingPosition()
    {
        Transform road = transform.parent != null ? transform.parent.Find("FuchuLocalRoads_1000m") : null;
        if (road == null || !road.gameObject.activeInHierarchy ||
            !road.TryGetComponent(out MeshCollider spawnRoad) || !spawnRoad.enabled || spawnRoad.sharedMesh == null)
            return;

        Bounds roadBounds = spawnRoad.sharedMesh.bounds;
        Vector3 up = transform.up;
        Vector3 footOffset = transform.TransformVector(characterController.center - Vector3.up * (characterController.height * 0.5f));
        float radius = characterController.radius;
        float height = characterController.height;
        float lift = characterController.skinWidth + 0.02f;
        var validPositions = new List<Vector3>(SpawnPoints.Length);

        characterController.enabled = false;
        Physics.SyncTransforms();
        foreach (Vector2 point in SpawnPoints)
        {
            Vector3 rayStart = road.TransformPoint(new Vector3(point.x, roadBounds.max.y + 1f, point.y));
            if (!spawnRoad.Raycast(new Ray(rayStart, -road.up), out RaycastHit hit, roadBounds.size.y + 2f) ||
                Vector3.Angle(hit.normal, up) > characterController.slopeLimit)
                continue;

            Vector3 feet = hit.point + up * lift;
            Vector3 lower = feet + up * radius;
            Vector3 upper = feet + up * (height - radius);
            if (!Physics.CheckCapsule(lower, upper, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                validPositions.Add(feet - footOffset);
        }
        if (validPositions.Count > 0)
        {
            transform.position = validPositions[Random.Range(0, validPositions.Count)];
            startingPosition = transform.localPosition;
        }
        else
        {
            Debug.LogWarning("開始地点の10候補に立てる場所が見つからなかったため、従来の開始地点を使います。", this);
        }
        characterController.enabled = true;
    }

    private void Update()
    {
        if (!IsPlaying) return;
        if (RemainingRunSeconds <= 0.0)
        {
            FinishRun();
            return;
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame && walkHud != null && walkHud.isActiveAndEnabled)
        {
            pausedRunSeconds = RemainingRunSeconds;
            PauseForMenu();
            return;
        }

        if (keyboard.f5Key.wasPressedThisFrame)
        {
            ReturnToStartingPosition();
            return;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked &&
            (walkHud == null || !walkHud.TimeMenuVisible))
        {
            LockCursor();
        }

        if (Cursor.lockState == CursorLockMode.Locked && mouse != null && cameraTransform != null)
        {
            Vector2 mouseDelta = mouse.delta.ReadValue() * lookSensitivity;
            transform.Rotate(0f, mouseDelta.x, 0f);
            cameraPitch = Mathf.Clamp(cameraPitch - mouseDelta.y, MinCameraPitch, MaxCameraPitch);
        }

        Vector2 input = Vector2.zero;
        if (keyboard.wKey.isPressed) input.y += 1f;
        if (keyboard.sKey.isPressed) input.y -= 1f;
        if (keyboard.dKey.isPressed) input.x += 1f;
        if (keyboard.aKey.isPressed) input.x -= 1f;
        input = Vector2.ClampMagnitude(input, 1f);

        bool sprinting = Stamina > 0.0 && input != Vector2.zero &&
            (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        if (sprinting)
        {
            float targetBonus = (float)System.Math.Min(MaximumSprintBonus, System.Math.Floor(BuildingPoints / PointsPerSprintBonus));
            sprintBonus = Mathf.MoveTowards(sprintBonus, targetBonus, SprintBonusPerSecond * Time.deltaTime);
        }
        else
        {
            sprintBonus = 0f;
        }
        float speed = sprinting ? sprintSpeed + sprintBonus : walkSpeed;
        if (Time.time < slowedUntil) speed *= 0.5f;
        Vector3 horizontalMove = (transform.right * input.x + transform.forward * input.y) * speed;

        if (characterController.isGrounded)
        {
            verticalSpeed = -2f;
            if (keyboard.spaceKey.wasPressedThisFrame)
                verticalSpeed = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
        else
        {
            verticalSpeed += gravity * Time.deltaTime;
        }

        Vector3 velocity = horizontalMove + Vector3.up * verticalSpeed;
        characterController.Move(velocity * Time.deltaTime);

        float groundSpeed = Vector3.ProjectOnPlane(characterController.velocity, transform.up).magnitude;
        if (sprinting && groundSpeed > 0.05f)
            Stamina = System.Math.Max(0.0, Stamina - SprintStaminaPerSecond * Time.deltaTime);
        if (groundSpeed > 0.05f) territory.RecordGroundStep(transform.position);
        CreditBuildingPoints();
        if (characterController.isGrounded && groundSpeed > 0.05f)
        {
            stepPhase = Mathf.Repeat(stepPhase + groundSpeed * Time.deltaTime * 3f, Mathf.PI * 2f);
            playerMesh.transform.localPosition = Vector3.up * (playerFootHeight + Mathf.Abs(Mathf.Sin(stepPhase)) * 0.025f);
        }
        else
        {
            stepPhase = 0f;
            playerMesh.transform.localPosition = Vector3.up * playerFootHeight;
        }
    }

    private void CreditBuildingPoints()
    {
        double points = territory.BuildingPoints;
        if (points <= BuildingPoints) return;
        BuildingPoints = points;
    }

    internal void RecoverStamina(double fraction)
    {
        if (!IsPlaying) return;
        CreditBuildingPoints();
        Stamina = System.Math.Min(MaxStamina, Stamina + MaxStamina * fraction);
    }

    internal void LoseStamina(double fraction)
    {
        if (!IsPlaying) return;
        Stamina = System.Math.Max(0.0, Stamina - MaxStamina * fraction);
    }

    internal void HitResident()
    {
        LoseStamina(0.01);
        SlowTemporarily();
    }

    internal void SlowTemporarily()
    {
        if (!IsPlaying) return;
        slowedUntil = Time.time + 1f;
    }

    private void FinishRun()
    {
        RunFinished = true;
        territory.FinishRun();
        CreditBuildingPoints();
        PauseForMenu();
        Ranking = FuchuRunRanking.Record(TerritoryCoverage, BuildingPoints, out int rank);
        RunRank = rank;
    }

    private void PauseForMenu()
    {
        timeScaleBeforeMenu = Time.timeScale;
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void ResumeFromMenu()
    {
        if (!timeScaleBeforeMenu.HasValue) return;
        Time.timeScale = timeScaleBeforeMenu.Value;
        timeScaleBeforeMenu = null;
    }

    internal void ContinueRun()
    {
        if (!IsPaused || ReloadPending) return;
        runDeadline = Time.realtimeSinceStartupAsDouble + pausedRunSeconds.Value;
        pausedRunSeconds = null;
        ResumeFromMenu();
        if (walkHud == null || !walkHud.TimeMenuVisible) LockCursor();
    }

    internal void ReloadRun(bool startImmediately)
    {
        if ((!RunFinished && !IsPaused) || ReloadPending) return;
        int sceneIndex = gameObject.scene.buildIndex;
        if (sceneIndex < 0)
        {
            Debug.LogWarning("RetryとHomeには現在のシーンをビルド設定に追加する必要があります。", this);
            return;
        }
        startAfterReload = startImmediately;
        ReloadPending = true;
        SceneManager.LoadScene(sceneIndex);
    }

    private void LateUpdate()
    {
        if (!IsPaused) UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (cameraTransform == null)
            return;

        Quaternion viewRotation = transform.rotation * Quaternion.Euler(cameraPitch, 0f, 0f);
        // Keep the center ray above the player even when the camera retracts near a wall.
        Vector3 pivot = transform.TransformPoint(characterController.center +
            Vector3.up * (characterController.height * 0.5f + CameraClearanceAbovePlayer));
        Vector3 direction = viewRotation * Vector3.back;
        float distance = CameraDistance;
        if (Physics.SphereCast(pivot, CameraCollisionRadius, direction, out RaycastHit hit, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(0f, hit.distance - 0.04f);

        cameraTransform.SetPositionAndRotation(pivot + direction * distance, viewRotation);
    }

    private void OnDisable()
    {
        sprintBonus = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void OnDestroy()
    {
        ResumeFromMenu();
        if (territory != null) Destroy(territory.gameObject);
        if (playerRenderer != null)
        {
            foreach (Material material in playerRenderer.sharedMaterials)
                Destroy(material);
        }
        if (playerMesh != null)
        {
            Destroy(playerMesh.sharedMesh);
            Destroy(playerMesh.gameObject);
        }
    }

    public void ReturnToStartingPosition()
    {
        if (!IsPlaying) return;
        slowedUntil = 0f;
        if (territory != null) territory.ClearOpenTrail();
        sprintBonus = 0f;
        characterController.enabled = false;
        transform.SetLocalPositionAndRotation(startingPosition, startingRotation);
        cameraPitch = DefaultCameraPitch;
        stepPhase = 0f;
        playerMesh.transform.localPosition = Vector3.up * playerFootHeight;
        verticalSpeed = 0f;
        characterController.enabled = true;
        UpdateCamera();
    }

    private static void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}

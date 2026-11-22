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
    internal const double BaseStamina = 1000.0;
    internal const float SprintStaminaPerSecond = 20f;
    internal const double PointsPerSprintBonus = 100.0;
    internal const float MaximumSprintBonus = 30f;
    internal const float SprintBonusPerSecond = 5f;

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
    [SerializeField] private float walkSpeed = 6.5f;
    [SerializeField] private float sprintSpeed = 9f;
    [SerializeField] private float lookSensitivity = 0.12f;
    [SerializeField] private float jumpHeight = 1.65f;
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
    private float? timeScaleBeforeMenu;
    private bool runStarted;
    private static bool startAfterReload;
    internal bool OnlineSprintRequested { get; private set; }
    internal uint OnlineCorrectionVersion { get; private set; }
    private Vector3 onlineStartingPosition;
    private Quaternion onlineStartingRotation;
    internal void ShowOnlineResults()
    {
        MenuOpen = true; OnlineSprintRequested = false; sprintBonus = 0;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }
    private double unsentOnlineStaminaCost;
    private readonly Queue<(uint Sequence, double Cost)> onlineStaminaCosts = new Queue<(uint, double)>();

    internal void RegisterOnlinePoseSent(uint sequence)
    {
        onlineStaminaCosts.Enqueue((sequence, unsentOnlineStaminaCost));
        unsentOnlineStaminaCost = 0.0;
        while (onlineStaminaCosts.Count > 128) onlineStaminaCosts.Dequeue();
    }
    internal void ApplyOnlineGrowth(FuchuOnlineTerritoryState.Participant growth)
    {
        if (growth.CorrectionVersion != OnlineCorrectionVersion)
        {
            OnlineCorrectionVersion = growth.CorrectionVersion;
            onlineStaminaCosts.Clear(); unsentOnlineStaminaCost = 0;
            characterController.enabled = false;
            transform.localPosition = growth.CorrectionPosition;
            characterController.enabled = true;
            verticalSpeed = sprintBonus = 0;
            Online.ResetWalkingTrail();
            UpdateCamera();
        }
        while (onlineStaminaCosts.Count > 0 && onlineStaminaCosts.Peek().Sequence <= growth.AcknowledgedPose)
            onlineStaminaCosts.Dequeue();
        double debt = unsentOnlineStaminaCost;
        foreach (var pending in onlineStaminaCosts) debt += pending.Cost;
        Stamina = System.Math.Max(0.0, System.Math.Min(growth.MaxStamina, growth.Stamina - debt));
        sprintBonus = Mathf.Min(sprintBonus, growth.SpeedLevel - 1);
    }

    public double Stamina { get; private set; } = BaseStamina;
    public double StaminaCapacityPoints => IsOnlineExploration ? Online.LocalGrowth?.StaminaCapacityPoints ?? 0.0 : territory != null ? territory.StaminaCapacityPoints : 0.0;
    public double MaxStamina => BaseStamina + StaminaCapacityPoints;
    public double BuildingPoints => IsOnlineExploration ? Online.LocalGrowth?.BuildingPoints ?? 0.0 : territory != null ? territory.BuildingPoints : 0.0;
    public double MovementPoints => IsOnlineExploration ? Online.LocalGrowth?.MovementPoints ?? 0.0 : territory != null ? territory.MovementPoints : 0.0;
    public int SpeedLevel => 1 + (int)System.Math.Min(MaximumSprintBonus, System.Math.Floor(MovementPoints / PointsPerSprintBonus));
    public double ToughnessPoints => IsOnlineExploration ? Online.LocalGrowth?.ToughnessPoints ?? 0.0 : territory != null ? territory.ToughnessPoints : 0.0;
    internal FuchuOnlineConnection Online { get; private set; }
    internal bool IsOnlineExploration => Online != null && Online.Exploring;
    private readonly List<(Behaviour Component, bool Enabled)> offlineBehaviours = new List<(Behaviour, bool)>();
    private readonly List<(GameObject Object, bool Active)> offlineObjects = new List<(GameObject, bool)>();
    internal FuchuBattleMatch Battle { get; private set; }
    internal FuchuBuildingEffects BuildingEffects { get; private set; }
    public int ToughnessLevel => 1 + (int)System.Math.Min(5.0, System.Math.Floor(ToughnessPoints / 500.0));
    internal FuchuStaminaItems RecoveryItems { get; private set; }
    internal float WalkingSpeed => walkSpeed;
    internal float BaseSprintSpeed => sprintSpeed;
    public double TerritoryCoverage => IsOnlineExploration ? Online.LocalCoverage : territory != null ? territory.Coverage : 0.0;
    internal bool RunFinished { get; private set; }
    internal bool IsPlaying => runStarted && !RunFinished;
    internal bool MenuOpen { get; private set; }
    internal bool ReloadPending { get; private set; }
    internal int RunRank { get; private set; }
    internal IReadOnlyList<FuchuRunRanking.Entry> Ranking { get; private set; }
    internal double RemainingRunSeconds => IsOnlineExploration ? Online.RemainingSeconds : !runStarted || RunFinished ? 0.0 :
        System.Math.Max(0.0, runDeadline - Time.realtimeSinceStartupAsDouble);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetReloadState() => startAfterReload = false;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        BuildingEffects = gameObject.AddComponent<FuchuBuildingEffects>();
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
        Battle = gameObject.AddComponent<FuchuBattleMatch>();
        territory = Battle.Initialize(this, playerRenderer);
        var recoveryItems = new GameObject("Stamina Recovery Items");
        recoveryItems.transform.SetParent(transform.parent, false);
        RecoveryItems = recoveryItems.AddComponent<FuchuStaminaItems>();
        RecoveryItems.Initialize(this, characterController);
        UpdateCamera();
        Online = gameObject.AddComponent<FuchuOnlineConnection>();
        Online.Initialize(this, playerRenderer);
        bool startImmediately = startAfterReload;
        startAfterReload = false;
        if (startImmediately || walkHud == null || !walkHud.isActiveAndEnabled)
            BeginRun();
        else
            PauseForMenu();
    }

    internal void BeginRun()
    {
        if (runStarted || (Online != null && Online.IsBusy)) return;
        runStarted = true;
        ResumeFromMenu();
        runDeadline = Time.realtimeSinceStartupAsDouble + RunDurationSeconds;
        LockCursor();
    }

    internal void EnterOnlineExploration()
    {
        // Online state is separate from the offline match and its local ranking.
        onlineStartingPosition = transform.localPosition;
        onlineStartingRotation = transform.localRotation;
        offlineBehaviours.Clear();
        offlineObjects.Clear();
        Disable(territory);
        foreach (FuchuTrafficSpawner traffic in FindObjectsByType<FuchuTrafficSpawner>(FindObjectsSortMode.None)) Disable(traffic);
        foreach (FuchuResidentSpawner resident in FindObjectsByType<FuchuResidentSpawner>(FindObjectsSortMode.None)) Disable(resident);
        foreach (FuchuBattleMatch.Participant participant in Battle.Players)
            if (participant.Id != 0 && participant.Character != null) Hide(participant.Character.gameObject);
        if (RecoveryItems != null) Hide(RecoveryItems.gameObject);
        ResumeFromMenu();
        runStarted = true;
        RunFinished = false;
        MenuOpen = true;
        Stamina = BaseStamina;
        onlineStaminaCosts.Clear(); unsentOnlineStaminaCost = 0.0; OnlineSprintRequested = false; OnlineCorrectionVersion = 0;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        void Disable(Behaviour component)
        {
            if (component == null) return;
            offlineBehaviours.Add((component, component.enabled));
            component.enabled = false;
        }
        void Hide(GameObject obj)
        {
            offlineObjects.Add((obj, obj.activeSelf));
            obj.SetActive(false);
        }
    }

    internal void ExitOnlineExploration()
    {
        runStarted = false;
        MenuOpen = false;
        RunFinished = false;
        sprintBonus = verticalSpeed = 0f;
        Stamina = BaseStamina;
        onlineStaminaCosts.Clear(); unsentOnlineStaminaCost = 0.0; OnlineSprintRequested = false; OnlineCorrectionVersion = 0;
        foreach (var entry in offlineBehaviours)
            if (entry.Component != null) entry.Component.enabled = entry.Enabled;
        foreach (var entry in offlineObjects)
            if (entry.Object != null) entry.Object.SetActive(entry.Active);
        offlineBehaviours.Clear();
        offlineObjects.Clear();
        characterController.enabled = false;
        transform.SetLocalPositionAndRotation(startingPosition, startingRotation);
        characterController.enabled = true;
        cameraPitch = DefaultCameraPitch;
        UpdateCamera();
        PauseForMenu();
    }

    internal void PlaceOnlineNear(Vector3 hostPosition, float yaw)
    {
        if (!IsOnlineExploration) return;
        Vector3 candidate = hostPosition + Quaternion.Euler(0f, yaw, 0f) * Vector3.right * 2f;
        Transform root = transform.parent;
        Transform road = root != null ? root.Find("FuchuLocalRoads_1000m") : null;
        characterController.enabled = false;
        Vector3 target = root != null ? root.TransformPoint(hostPosition) : hostPosition;
        if (road != null && road.TryGetComponent(out Collider surface))
        {
            Vector3 point = root.TransformPoint(candidate);
            if (surface.Raycast(new Ray(point + transform.up * 20f, -transform.up), out RaycastHit hit, 40f))
            {
                Vector3 feet = hit.point + transform.up * (characterController.skinWidth + 0.02f);
                float radius = characterController.radius;
                if (!Physics.CheckCapsule(feet + transform.up * radius,
                    feet + transform.up * (characterController.height - radius), radius,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    Vector3 footOffset = transform.TransformVector(characterController.center - Vector3.up * characterController.height * 0.5f);
                    target = feet - footOffset;
                }
            }
        }
        transform.position = target;
        transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        onlineStartingPosition = transform.localPosition;
        onlineStartingRotation = transform.localRotation;
        characterController.enabled = true;
        verticalSpeed = 0f;
        Online.ResetWalkingTrail();
        UpdateCamera();
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
        if (!IsOnlineExploration && RemainingRunSeconds <= 0.0)
        {
            FinishRun();
            return;
        }

        SyncOwnedGrowth();
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame && walkHud != null && walkHud.isActiveAndEnabled)
        {
            if (MenuOpen) ContinueRun();
            else
            {
                MenuOpen = true;
                sprintBonus = 0f;
                OnlineSprintRequested = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            return;
        }

        if (IsOnlineExploration && !Online.MatchPlaying)
        { OnlineSprintRequested = false; return; }

        if (!MenuOpen && keyboard.f5Key.wasPressedThisFrame)
        {
            ReturnToStartingPosition();
            return;
        }

        Mouse mouse = Mouse.current;
        if (!MenuOpen && mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked &&
            (walkHud == null || !walkHud.TimeMenuVisible))
        {
            LockCursor();
        }

        if (!MenuOpen && Cursor.lockState == CursorLockMode.Locked && mouse != null && cameraTransform != null)
        {
            Vector2 mouseDelta = mouse.delta.ReadValue() * lookSensitivity;
            transform.Rotate(0f, mouseDelta.x, 0f);
            cameraPitch = Mathf.Clamp(cameraPitch - mouseDelta.y, MinCameraPitch, MaxCameraPitch);
        }

        Vector2 input = Vector2.zero;
        if (!MenuOpen && keyboard.wKey.isPressed) input.y += 1f;
        if (!MenuOpen && keyboard.sKey.isPressed) input.y -= 1f;
        if (!MenuOpen && keyboard.dKey.isPressed) input.x += 1f;
        if (!MenuOpen && keyboard.aKey.isPressed) input.x -= 1f;
        input = Vector2.ClampMagnitude(input, 1f);

        OnlineSprintRequested = input != Vector2.zero && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        bool sprinting = Stamina > 0.0 && OnlineSprintRequested;
        if (sprinting)
        {
            float targetBonus = SpeedLevel - 1;
            sprintBonus = Mathf.MoveTowards(sprintBonus, targetBonus, SprintBonusPerSecond * Time.deltaTime);
        }
        else
        {
            sprintBonus = 0f;
        }
        float speed = sprinting ? sprintSpeed + sprintBonus : walkSpeed;
        if (IsOnlineExploration ? Online.LocalSlowed : Time.time < slowedUntil) speed *= 0.5f;
        Vector3 horizontalMove = (transform.right * input.x + transform.forward * input.y) * speed;

        if (characterController.isGrounded)
        {
            verticalSpeed = -2f;
            if (!MenuOpen && keyboard.spaceKey.wasPressedThisFrame)
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
        {
            double spent = System.Math.Min(Stamina, SprintStaminaPerSecond * Time.deltaTime);
            Stamina -= spent;
            if (IsOnlineExploration) unsentOnlineStaminaCost += spent;
        }
        if (!IsOnlineExploration && groundSpeed > 0.05f) territory.RecordGroundStep(transform.position);
        SyncOwnedGrowth();
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

    internal void SyncOwnedGrowth()
    {
        Stamina = System.Math.Min(Stamina, MaxStamina);
        sprintBonus = Mathf.Min(sprintBonus, SpeedLevel - 1);
    }

    internal void RecoverStamina(double fraction)
    {
        if (!IsPlaying) return;
        SyncOwnedGrowth();
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
        MenuOpen = false;
        Battle.Finish();
        SyncOwnedGrowth();
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
        if (!MenuOpen || ReloadPending || RunFinished || IsOnlineExploration && !Online.TerritoryReady) return;
        MenuOpen = false;
        walkHud?.CloseGameplayMenu();
        if (walkHud == null || !walkHud.TimeMenuVisible) LockCursor();
    }

    internal void ReloadRun(bool startImmediately)
    {
        if (IsOnlineExploration || (!RunFinished && !MenuOpen) || ReloadPending) return;
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
        UpdateCamera();
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
        transform.SetLocalPositionAndRotation(IsOnlineExploration ? onlineStartingPosition : startingPosition,
            IsOnlineExploration ? onlineStartingRotation : startingRotation);
        cameraPitch = DefaultCameraPitch;
        stepPhase = 0f;
        playerMesh.transform.localPosition = Vector3.up * playerFootHeight;
        verticalSpeed = 0f;
        characterController.enabled = true;
        if (IsOnlineExploration) Online.ResetWalkingTrail();
        UpdateCamera();
    }

    private static void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}

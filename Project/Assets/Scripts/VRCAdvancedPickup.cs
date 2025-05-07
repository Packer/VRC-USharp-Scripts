//Created by PackerB ©2023, GPL-3.0 license 
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual), RequireComponent(typeof(Rigidbody), typeof(VRCPickup))]
public class VRCAdvancedPickup : UdonSharpBehaviour
{
    [Header("Networked")]
    [UdonSynced, FieldChangeCallback(nameof(PlayerId))]
    private int playerId = -1;

    [UdonSynced, FieldChangeCallback(nameof(PlayerBone))]
    private int playerBone;

    [UdonSynced, FieldChangeCallback(nameof(OffsetRotation))]
    private Quaternion offsetRotation;

    [UdonSynced, FieldChangeCallback(nameof(OffsetPosition))]
    private Vector3 offsetPosition;

    [Header("Synchronization")]
    [Range(1, 60), SerializeField, Tooltip("Network updates a second, higher uses more network speed (Default - 2), Only set higher if you need accurate response time")]
    private float syncRate = 2;

    [Tooltip("If true, the rigidbody will sync over the network when the pickup is dropped")]
    public bool syncRigidbody = true;

    private bool syncEnabled = false;           //State of syncing
    [FieldChangeCallback(nameof(SyncPeriod))]
    private float syncPeriod = 0;               //Period when syncing can happen
    [FieldChangeCallback(nameof(SyncDelay))]
    private float syncDelay = 0;                //Time remain until the next sync
    private float syncTick => 1 / syncRate;     //Time between each sync
    private float syncPickupGrace = 3f;       //Pickup Grace - const (3000ms)

    //[Tooltip("Disables the Frozen state when this is picked up")]
    //public bool unfreezeOnPickup = true;

    [UdonSynced, FieldChangeCallback(nameof(Frozen))]
    public bool frozen = false;    //Stop doing Update/PostUpdate calculations on this script

    [Tooltip("Automatically makes pickup unfrozen when pickup or TeleportTo"), HideInInspector]
    public bool automaticUnfreeze = true;

    //Object Sync Functions
    [FieldChangeCallback(nameof(GravityState))]
    private bool gravityState = true;

    [FieldChangeCallback(nameof(KinematicState))]
    private bool kinematicState = true;

    //Synced Rigidbody Infomation
    [UdonSynced]
    private Vector3 syncPosition = Vector3.zero;
    [UdonSynced]
    private Quaternion syncRotation = Quaternion.identity;
    [UdonSynced]
    private Vector3 syncVelocity = Vector3.zero;
    [UdonSynced]
    private Vector3 syncAngularVelocity = Vector3.zero;

    //Last Positions
    private Vector3 lastPosition = Vector3.zero;
    private Quaternion lastRotation = Quaternion.identity;
    private Vector3 lastVelocity = Vector3.zero;
    private Vector3 lastAngularVelocity = Vector3.zero;

    // Private
    private Rigidbody rb;
    private VRC_Pickup pickup;
    private VRCPlayerApi playerAPI;
    private bool kinematicDefault = false;
    private bool pickupable = false;
    private bool DebugTest = false;

    //Start Transform
    private Vector3 startPosition;
    private Quaternion startRotation;

    //--------------------------------
    //  Field Callbacks
    //--------------------------------

    public float SyncPeriod
    {
        set
        {
            syncPeriod = (value < 0) ? 0 : value;
            syncEnabled = (syncPeriod > 0) ? true : false;
        }
        get => syncPeriod;
    }

    public float SyncDelay
    {
        set
        {
            syncDelay = (value < 0) ? 0 : value;
        }
        get => syncDelay;
    }

    public Quaternion OffsetRotation
    {
        set
        {
            offsetRotation = value;
            RequestSerialization();
        }
        get => offsetRotation;
    }

    public Vector3 OffsetPosition
    {
        set
        {
            offsetPosition = value;
            RequestSerialization();
        }
        get => offsetPosition;
    }

    public int PlayerId
    {
        set
        {
            playerId = value;
            if (!HasPlayer())
            {
                if (DebugTest) Debug.Log("Player Held Reset");
                //Set Kinematic back to default
                KinematicState = kinematicDefault;
                pickup.pickupable = pickupable;
            }
            else if (playerId != Networking.LocalPlayer.playerId && pickup.DisallowTheft)
            {
                if (DebugTest) Debug.Log("Player Held No Theft");
                pickup.pickupable = false;
            }
            if (DebugTest) Debug.Log("PlayerID Changed to: " + value);
            RequestSerialization();
        }
        get => playerId;
    }

    public int PlayerBone
    {
        set
        {
            playerBone = value;
            if (DebugTest) Debug.Log("PlayerBone Changed to: " + value);
            RequestSerialization();
        }
        get => playerBone;
    }

    public bool GravityState
    {
        set
        {
            gravityState = value;
            if (rb != null)
                rb.useGravity = value;
            RequestSerialization();
        }
        get => gravityState;
    }

    public bool KinematicState
    {
        set
        {
            kinematicState = value;
            if (rb != null)
                rb.isKinematic = value;
            RequestSerialization();
        }
        get => kinematicState;
    }

    public bool Frozen
    {
        set
        {
            if (DebugTest) Debug.Log("Frozen: " + value);
            frozen = value;
            RequestSerialization();
        }
        get => frozen;
    }

    //--------------------------------
    //  Update Functions
    //--------------------------------

    private void Start()
    {
        //Respawn Location
        startPosition = transform.position;
        startRotation = transform.rotation;

        if (!pickup)
            pickup = GetComponent<VRC_Pickup>();

        if (!rb)
            rb = GetComponent<Rigidbody>();

        //Default States
        pickupable = pickup.pickupable;
        KinematicState = kinematicDefault = rb.isKinematic;
        GravityState = rb.useGravity;

        //Intial Position/Rotation Sync
        if (Networking.IsOwner(gameObject)) //Owner Sync
        {
            if (IsHeld())
                SyncOffsets();
            else
                SyncOwnerRigidbody();

            //Freeze after first sync so its up to date
            //SetFrozen(true);
        }
        else //Client Sync
        {
            if (IsHeld()) //Not Held
            {
                if (DebugTest) 
					Debug.Log("-----------------------Client: OFFSET SETUP");

                //Update Location
                rb.MoveRotation(GetBoneRotation() * OffsetRotation);
                rb.MovePosition((GetBoneRotation() * OffsetPosition) + GetBonePosition());
            }
            else //Held
            {
                if (DebugTest) 
					Debug.Log("--------------------Client:  Rigid");
                UpdateClientRigidbody();
            }
        }
    }

    public void FixedUpdate()
    {
        //if (!Networking.IsOwner(gameObject))
        //    Debug.Log("Frozen: " + Frozen);
        if (Frozen)
            return;

        //Input Rotation Check
        if (SyncPeriod <= 0 && Networking.LocalPlayer != null && PlayerId == Networking.LocalPlayer.playerId)
        {
            if (Input.GetKey(KeyCode.I) || Input.GetKey(KeyCode.J) || Input.GetKey(KeyCode.K)
                || Input.GetKey(KeyCode.L) || Input.GetKey(KeyCode.U) || Input.GetKey(KeyCode.O))
            {
                //Update Once
                SyncPeriod = syncTick;
            }
        }

        //Client Rigidbody Update
        if (RigidbodyReady())
        {
            //Only Clients Update
            UpdateClientRigidbody();

            //Rigidbody has moved
            if (Networking.IsOwner(gameObject) && SyncPeriod <= 0 && syncPosition != rb.position)
                SyncPeriod = syncTick;
        }

        //Sync Update
        if (syncEnabled)
        {
            //Reduce by Frame Time
            SyncPeriod -= Time.fixedDeltaTime;
            SyncDelay -= Time.fixedDeltaTime;

            //Trigger Sync
            if (SyncDelay <= 0)
            {
                SyncDelay = syncTick;

                //Not Held - Sync Rigidbody
                if (RigidbodyReady())
                    SyncOwnerRigidbody();
                else //Being Held - Sync Held Offsets
                    SyncOffsets();
            }
        }
    }

    public override void PostLateUpdate()
    {
        //No Network player - Joining/Leaving Game Safety
        if (Frozen || Networking.LocalPlayer == null)
            return;

        //Make sure the correct bone is in use
        if (Networking.IsOwner(gameObject))
        {
            if (HasPlayer() && PlayerBone != (int)PickupHandToHumanBone(pickup.currentHand))
                PlayerBone = (int)PickupHandToHumanBone(pickup.currentHand);
        }
        else //Not Owner
        {
            //Holding but its not ours
            if (pickup.IsHeld && pickup.currentPlayer.playerId != PlayerId)
                pickup.Drop();
        }

        //If Held by Local player
        if (PlayerId == Networking.LocalPlayer.playerId)
            return;

        //If pickup is being held and the player is in game
        if (HasPlayer())
        {
            //Set to Kinematic on Clients
            if (rb.isKinematic != true)
                rb.isKinematic = true;

            //Update Location
            rb.MoveRotation(GetBoneRotation() * OffsetRotation);
            rb.MovePosition((GetBoneRotation() * OffsetPosition) + GetBonePosition());
        }
    }

    //--------------------------------
    //  Override Functions
    //--------------------------------

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        SyncPeriod = syncPickupGrace;
        if (pickup.IsHeld)
            PlayerId = player.playerId;
        else
            PlayerId = -1;
    }

    public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
    {
        //Same person
        if (requestingPlayer.playerId == requestedOwner.playerId)
            return true;

        if (DebugTest) 
            Debug.Log("Ownership Requested by: " + requestingPlayer.playerId + " from: " + requestedOwner.playerId);

        //Called on the CURRENT owner by the Requestee
        if (pickup.IsHeld && pickup.DisallowTheft)
            return false;
        else
        {
            //Old Owner Drop Pickup
            pickup.Drop();
            OnDrop();

            //Assign New Player
            PlayerId = -1;
            SyncPeriod = syncPickupGrace;

            return true;
        }
    }

    public override void OnPickup()
    {
        if (DebugTest) Debug.Log("OnPickup - Owner Only");

        //New Ownership
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        else
            SetPlayer();
    }

    public override void OnDrop()
    {
        if (DebugTest) Debug.Log("OnDrop - Owner Only");

        //Set Kinematic back to default
        KinematicState = kinematicDefault;

        //Default Values
        PlayerId = -1;
        PlayerBone = -1;

        //Initial Rigidbody Sync
        if (RigidbodyReady())
            SyncOwnerRigidbody();
    }

    //--------------------------------
    //  Sync
    //--------------------------------

    void SetPlayer()
    {
        if (DebugTest) Debug.Log("SetPlayer - Owner Only");

        if (!Networking.IsOwner(gameObject))
            return;

        //Unfreeze the Networking on this object
        if(automaticUnfreeze)
            SetFrozen(false);

        if (DebugTest) Debug.Log("SetPlayer Pre Values - ID: " + PlayerId + " Bone: " + PlayerBone);

        //Assign Players values
        PlayerId = Networking.LocalPlayer.playerId;
        PlayerBone = (int)PickupHandToHumanBone(pickup.currentHand);

        if (DebugTest) Debug.Log("SetPlayer POST Values - ID: " + PlayerId + " Bone: " + PlayerBone);

        //Update Period
        SyncPeriod = syncPickupGrace;

        //Intial Sync
        SyncOffsets();

        RequestSerialization();
    }

    void SyncOffsets()
    {
        //Generate Offsets to send to clients
        if (Networking.IsOwner(gameObject))
        {
            Quaternion inverse = Quaternion.Inverse(GetBoneRotation());
            OffsetRotation = inverse * rb.rotation;
            OffsetPosition = inverse * (rb.position - GetBonePosition());
        }
    }

    void UpdateClientRigidbody()
    {
        //Client Only
        if (!Networking.IsOwner(gameObject))
        {
            //--------------------------------
            //Client
            //--------------------------------
            //Position
            if (lastPosition != syncPosition)
                rb.position = lastPosition = syncPosition;

            //Rotation
            if (lastRotation != syncRotation)
            {
                //Use more Network traffic on Quaternion to save on CPU conversion.
                rb.rotation = syncRotation;
                lastRotation = syncRotation;
            }

            //Velocity
            if (lastVelocity != syncVelocity)
                rb.velocity = lastVelocity = syncVelocity;

            //Angular Velocity
            if (lastAngularVelocity != syncAngularVelocity)
                rb.angularVelocity = lastAngularVelocity = syncAngularVelocity;
        }
    }

    private void SyncOwnerRigidbody()
    {
        //Update Rigidbody network vars
        if (Networking.IsOwner(gameObject))
        {
            //--------------------------------
            //Owner
            //--------------------------------
            bool ready = false; // Only update if something has updated

            //Position
            if (syncPosition != rb.position)
            {
                syncPosition = rb.position;
                ready = true;
            }

            //Rotation
            if (syncRotation != rb.rotation)
            {
                syncRotation = rb.rotation;
                ready = true;
            }

            //Velocity
            if (syncVelocity != rb.velocity)
            {
                syncVelocity = rb.velocity;
                ready = true;
            }

            //Angular Velocity
            if (syncAngularVelocity != rb.angularVelocity)
            {
                syncAngularVelocity = rb.angularVelocity;
                ready = true;
            }

            if (ready)
                RequestSerialization();
        }
    }

    void ForceSync()
    {
        if (!Networking.IsOwner(gameObject))
            return;


        if (DebugTest) Debug.Log("Force Sync" + rb.position);

        //Position
        syncPosition = rb.position;

        //Rotation
        syncRotation = rb.rotation;

        //Velocity
        syncVelocity = rb.velocity;

        //Angular Velocity
        syncAngularVelocity = rb.angularVelocity;

        RequestSerialization();
    }

    //--------------------------------
    //  Object Sync Functions
    //--------------------------------

    public void Respawn()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        rb.MovePosition(startPosition);
        rb.MoveRotation(startRotation);
        SyncOwnerRigidbody();
    }

    public void TeleportTo(Transform targetLocation)
    {
        TeleportTo(targetLocation.position, targetLocation.rotation);
    }

    public void TeleportTo(Vector3 targetPosition, Vector3 targetRotation)
    {
        TeleportTo(targetPosition, Quaternion.Euler(targetRotation));
    }

    public void TeleportTo(Vector3 targetPosition, Quaternion targetRotation)
    {
        if (!Networking.IsOwner(gameObject))
            return;
        if (!rb)
            rb = GetComponent<Rigidbody>();

        if (DebugTest) Debug.Log("Telporting to " + targetPosition);

        //Force Drop if held
        if (pickup.IsHeld)
        {
            OnDrop();
            pickup.Drop();
        }
        
        //Hack job, it works?
        transform.position = targetPosition;
        transform.rotation = targetRotation;

        //Rigidbody
        rb.MovePosition(targetPosition);
        rb.MoveRotation(targetRotation);

        if (!rb.isKinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        ForceSync();

        if(Frozen && automaticUnfreeze)
            SetFrozen(false);

        RequestSerialization();
    }

    /// <summary>
    /// Changes the gravity state, usually handled by the Rigidbody of the object but controlled here for sync purposes.
    /// </summary>
    /// <param name="value"></param>
    public void SetGravity(bool value)
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            return;

        GravityState = rb.useGravity = value;
    }

    /// <summary>
    /// Changes the kinematic state, usually handled by the Rigidbody of the object but controlled here for sync purposes. When the kinematic state is on, this Rigidbody ignores forces, collisions and joints.
    /// </summary>
    /// <param name="value"></param>
    public void SetKinematic(bool value)
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            return;

        KinematicState = value;
    }

    /// <summary>
    /// Freezes all Update and PostUpdate networking calculations, methods still work. Use this for performance boosts.
    /// </summary>
    /// <param name="value"></param>
    public void SetFrozen(bool value)
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            return;
        Frozen = value;
    }

    /// <summary>
    /// Sets both Advanced Pickup and Pickup components pickupable state, this becomes the default when assigning to a new player.
    /// </summary>
    /// <param name="value"></param>
    public void SetPickupable(bool value)
    {
        pickupable = pickup.pickupable = value;
    }

    //-------------------------------------------------------------------------
    // Checks
    //-------------------------------------------------------------------------

    int HumanBoneToPickupHand(HumanBodyBones bone)
    {
        if (bone == HumanBodyBones.LeftHand)
            return 1;
        else if (bone == HumanBodyBones.RightHand)
            return 2;

        return 0;
    }

    ///Returns VRCPickup.PickupHand value in HumanBodyBones <summary>
    /// Returns VRCPickup.PickupHand value in HumanBodyBones
    /// </summary>
    /// <param name="i"></param>
    /// <returns></returns>
    HumanBodyBones PickupHandToHumanBone(VRCPickup.PickupHand i)
    {
        if (i == VRCPickup.PickupHand.Left)
            return HumanBodyBones.LeftHand;
        else if (i == VRCPickup.PickupHand.Right)
            return HumanBodyBones.RightHand;

        return 0;
    }

    VRCPlayerApi GetAPI()
    {
        if (playerAPI == null || playerAPI.playerId < 1 || playerAPI.playerId != PlayerId)
            playerAPI = VRCPlayerApi.GetPlayerById(PlayerId);

        return playerAPI;
    }

    /// <summary>
    /// Returns true if a player is found
    /// </summary>
    /// <returns></returns>
    bool HasPlayer()
    {
        if (PlayerId > 0 && GetAPI() != null)
            return true;
        else
            return false;
    }

    bool IsHeld()
    {
        if (PlayerId > 0 && pickup.IsHeld)
            return true;

        return false;
    }

    bool RigidbodyReady()
    {
        //!isKinematic && !rb.isSleeping
        if (syncRigidbody && PlayerId <= 0 && !pickup.IsHeld)
            return true;

        return false;
    }

    Vector3 GetBonePosition()
    {
        if (HasPlayer())
            return playerAPI.GetBonePosition((HumanBodyBones)PlayerBone);

        return Vector3.zero;
    }

    Quaternion GetBoneRotation()
    {
        if (HasPlayer())
            return playerAPI.GetBoneRotation((HumanBodyBones)PlayerBone);

        return Quaternion.identity;
    }
}

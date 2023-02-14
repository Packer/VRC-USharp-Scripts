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
    private float syncPickupGrace = 3f;       //Pickup Grace - const (300ms)

    //Synced Rigidbody Infomation
    [UdonSynced]
    private Vector3 syncPosition = Vector3.zero;
    [UdonSynced]
    private Vector3 syncRotation = Vector3.zero;
    [UdonSynced]
    private Vector3 syncVelocity = Vector3.zero;
    [UdonSynced]
    private Vector3 syncAngularVelocity = Vector3.zero;

    //Last Positions
    private Vector3 lastPosition = Vector3.zero;
    private Vector3 lastRotation = Vector3.zero;
    private Vector3 lastVelocity = Vector3.zero;
    private Vector3 lastAngularVelocity = Vector3.zero;

    // Private
    private Rigidbody rb;
    private VRC_Pickup pickup;
    private VRCPlayerApi playerAPI;
    private bool kinematicDefault = false;
    private bool DebugTest = false;

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
                if(DebugTest) Debug.Log("Player Held Reset");
                //Set Kinematic back to default
                rb.isKinematic = kinematicDefault;
                pickup.pickupable = true;
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


    //--------------------------------
    //  Functions
    //--------------------------------

    private void Start()
    {
        if (!pickup)
            pickup = GetComponent<VRC_Pickup>();

        if (!rb)
            rb = GetComponent<Rigidbody>();

        kinematicDefault = rb.isKinematic;
    }

    //OWNER
    public override void OnPickup()
    {
        if (DebugTest) Debug.Log("OnPickup - Owner Only");

        //New Ownership
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        else
            SetPlayer();
    }

    void SetPlayer()
    {
        if (DebugTest) Debug.Log("SetPlayer - Owner Only");

        if (!Networking.IsOwner(gameObject))
            return;

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

    public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
    {
        if (DebugTest) Debug.Log("Ownership Requested by: " + requestingPlayer.playerId + " from: " + requestedOwner.playerId);

        if (pickup.DisallowTheft)
            return false;
        else
        {
            //Drop Pickup
            pickup.Drop();

            //Assign New Player
            PlayerId = requestingPlayer.playerId;
            SyncPeriod = syncPickupGrace;

            return true;
        }
    }

    /*
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (DebugTest) Debug.Log("Onwership transfered to: " + player.playerId);    

        //if (Networking.LocalPlayer == player)
        //    SetPlayer();
    }
    */

    //OWNER
    public override void OnDrop()
    {
        if (DebugTest) Debug.Log("OnDrop - Owner Only");

        //Set Kinematic back to default
        rb.isKinematic = kinematicDefault;

        //Default Values
        PlayerId = -1;
        PlayerBone = -1;

        //Initial Rigidbody Sync
        if (RigidbodyReady())
            SyncOwnerRigidbody();
    }

    public override void PostLateUpdate()
    {
        //No Network player - Joining/Leaving Game Safety
        if (Networking.LocalPlayer == null)
            return;

        //Make sure the correct bone is in use
        if (HasPlayer() && Networking.IsOwner(gameObject) && PlayerBone != (int)PickupHandToHumanBone(pickup.currentHand))
            PlayerBone = (int)PickupHandToHumanBone(pickup.currentHand);

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

    public void FixedUpdate()
    {
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

                //Sync Rigidbody
                if (RigidbodyReady())
                    SyncOwnerRigidbody();
                else
                    SyncOffsets();
            }
        }
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
                //TODO large scale test and compare CPU to Network traffic on Quaternion conversion
                rb.rotation = Quaternion.Euler(syncRotation);
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
            //Position
            if (syncPosition != rb.position)
                syncPosition = rb.position;

            //Rotation
            if (syncRotation != rb.rotation.eulerAngles)
                syncRotation = rb.rotation.eulerAngles;

            //Velocity
            if (syncVelocity != rb.velocity)
                syncVelocity = rb.velocity;

            //Angular Velocity
            if (syncAngularVelocity != rb.angularVelocity)
                syncAngularVelocity = rb.angularVelocity;

            RequestSerialization();
        }
    }

    //-------------------------------------------------------------------------
    // Functionality
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

    bool RigidbodyReady()
    {
        if (syncRigidbody && !rb.isKinematic && !rb.IsSleeping() && PlayerId <= 0)
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

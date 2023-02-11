
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

    [Header("Rigidbody Synchronization")]
    [Tooltip("If true, the rigidbody will sync over the network when the pickup is dropped")]
    public bool syncRigidbody = true;

    [Range(1, 60), SerializeField, Tooltip("Rigidbody network updates a second, higher uses more network speed (Default - 1), Only set higher if you need accurate response time")]
    private float syncRate = 1;

    private float syncNextTime = 0; //Time till the next sync
    
    //Synced Infomation
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
    private float setUpdateTime = 3.5f;
    private float currentUpdateTime = 0;

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
                Debug.Log("Player Held Reset");
                //Set Kinematic back to default
                rb.isKinematic = kinematicDefault;
                pickup.pickupable = true;
            }
            else if (playerId != Networking.LocalPlayer.playerId && pickup.DisallowTheft)
            {
                Debug.Log("Player Held No Theft");
                pickup.pickupable = false;
            }
            Debug.Log("PlayerID Changed to: " + value);
            RequestSerialization();
        }
        get => playerId;
    }

    public int PlayerBone
    {
        set
        {
            playerBone = value;
            //Debug.Log("PlayerBone Changed to: " + value);
            RequestSerialization();
        }
        get => playerBone;
    }

    private void Start()
    {
        if (!pickup)
            pickup = GetComponent<VRC_Pickup>();

        if (!rb)
            rb = GetComponent<Rigidbody>();

        kinematicDefault = rb.isKinematic;
    }

    //Only happens on OWNER
    public override void OnPickup()
    {
        Debug.Log("OnPickup - Owner Only");

        //New Ownership
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);

        //Debug.Log("Pickup Pre Values - ID: " + PlayerId + " Bone: " + PlayerBone);
        PlayerId = Networking.LocalPlayer.playerId;
        PlayerBone = (int)PickupHandToHumanBone(pickup.currentHand);
        //Debug.Log("Pickup POST Values - ID: " + PlayerId + " Bone: " + PlayerBone);

        //Local Update Time - Set to 5 sec to compenstate for Delay/Lag/Update 
        currentUpdateTime = setUpdateTime;

        RequestSerialization();
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
        {

            //Update offset rotation for the intial pickup time
            if (currentUpdateTime > 0)
            {
                currentUpdateTime -= Time.deltaTime;

                //Generate Offsets to send to clients
                Quaternion inverse = Quaternion.Inverse(GetBoneRotation());
                OffsetRotation = inverse * rb.rotation;
                OffsetPosition = inverse * (rb.position - GetBonePosition());
            }
            return;
        }

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
        else if (PlayerId >= 0 && VRCPlayerApi.GetPlayerById(PlayerId) == null)
            OnDrop();   
    }

    public void FixedUpdate()
    {
        //Rigidbody Released Syncing
        if(syncRigidbody)
            UpdateRigidbody();
    }

    void UpdateRigidbody()
    {
        //Sync Countdown on Owner
        if (Networking.IsOwner(gameObject))
        {
            if (syncNextTime > 0)
            {
                syncNextTime -= Time.fixedDeltaTime;
                return;
            }
        }

        //Ignore Rigidbody
        if (PlayerId > 0 || rb.IsSleeping())
        {
            return;
        }

        //Update Rigidbody network vars
        if (Networking.IsOwner(gameObject))
        {
            //Next Sync Update
            syncNextTime = 1 / syncRate;

            //--------------------------------
            //Owner
            //--------------------------------
            //Position
            if (syncPosition != rb.position)
                syncPosition = rb.position;

            //Rotation
            if(syncRotation != rb.rotation.eulerAngles)
                syncRotation = rb.rotation.eulerAngles;

            //Velocity
            if(syncVelocity != rb.velocity)
                syncVelocity = rb.velocity;

            //Angular Velocity
            if(syncAngularVelocity != rb.angularVelocity)
                syncAngularVelocity = rb.angularVelocity;

            RequestSerialization();
        }
        else
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
                //TODO large scale test compare CPU to Network traffic
                rb.rotation = Quaternion.Euler(syncRotation);
                lastRotation = syncRotation;
            }

            //Velocity
            if(lastVelocity != syncVelocity)
                rb.velocity = lastVelocity = syncVelocity;

            //Angular Velocity
            if(lastAngularVelocity != syncAngularVelocity)
                rb.angularVelocity = lastAngularVelocity = syncAngularVelocity;
        }
    }

    public void Update()
    {
        if (Networking.LocalPlayer != null && PlayerId == Networking.LocalPlayer.playerId)
        {
            if (Input.GetKeyDown(KeyCode.I) || Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.K) || Input.GetKeyDown(KeyCode.L))
            {
                currentUpdateTime = setUpdateTime;
            }
        }
    }

    //Only Happens on OWNER
    public override void OnDrop()
    {
        //Debug.Log("OnDrop - Owner Only");

        //Set Kinematic back to default
        rb.isKinematic = kinematicDefault;

        //Last Ownership reset
        if (Networking.IsOwner(gameObject))
        {
            //Default Values
            PlayerId = -1;
            PlayerBone = -1;
            OffsetRotation = Quaternion.identity;
        }
    }

    int GetHumanBone(HumanBodyBones i)
    {
        if (i == HumanBodyBones.LeftHand)
            return 1;
        else if (i == HumanBodyBones.RightHand)
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

    VRCPlayerApi GetAPI()
    {
        if (playerAPI == null || playerAPI.playerId < 1 || playerAPI.playerId != PlayerId)
            playerAPI = VRCPlayerApi.GetPlayerById(PlayerId);

        return playerAPI;

    }

    Vector3 GetBonePosition()
    {
        //TODO if get API is NOT used anywhere but fixed update, remove these checks
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

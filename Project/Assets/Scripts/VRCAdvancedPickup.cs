using UdonSharp;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual), RequireComponent(typeof(Rigidbody), typeof(VRCPickup))]
public class VRCAdvancedPickup : UdonSharpBehaviour
{

    [Header("Networked")]
    [SerializeField, UdonSynced, FieldChangeCallback(nameof(PlayerId))]
    private int playerId = -1;

    [SerializeField, UdonSynced, FieldChangeCallback(nameof(PlayerBone))]
    private int playerBone;

    [SerializeField, UdonSynced, FieldChangeCallback(nameof(OffsetRotation))]
    private Quaternion offsetRotation;

    [SerializeField, UdonSynced, FieldChangeCallback(nameof(OffsetPosition))]
    private Vector3 offsetPosition;

    //TODO Same as offset rotation but for Local Position offset for the grips/held as they don't change till you drop/swap hands

    [Range(1, 60), SerializeField, Tooltip("Networked updates a second, higher uses more network speed (Default 30)")]
    private float syncRate = 30;

    [SerializeField, FieldChangeCallback(nameof(SyncRigidbody))]
    private bool syncRigidbody = false;
    [SerializeField, UdonSynced]
    private Vector3 rbPosition = Vector3.zero;
    [SerializeField, UdonSynced]
    private Vector3 rbRotation = Vector3.zero;
    [SerializeField, UdonSynced]
    private Vector3 rbVelocity = Vector3.zero;
    [SerializeField, UdonSynced]
    private Vector3 rbAngular = Vector3.zero;


    
    private VRCPlayerApi playerAPI;
    private float intialUpdateTime = 0;

    private Vector3 gunRotationOffset = new Vector3(0,90,90);
    private Vector3 gripRotationOffset = new Vector3(0,0,90);

    public Vector3 gunOffset = new Vector3(0.03f, 0.13f, -0.04f);
    public Vector3 gripOffset = new Vector3(0.03f, 0.13f, -0.04f);


    [UdonSynced, HideInInspector]
    public Vector3 objectPosOffset;
    [UdonSynced, HideInInspector]
    public Quaternion objectRotOffset;


    // Private
    private Rigidbody rb;
    private VRC_Pickup pickup;

    private bool kinematicDefault = false;
    private bool gravityDefault = false;

    public bool SyncRigidbody
    {
        set
        {
            syncRigidbody = value;
            Debug.Log("SyncRigidbody");
            if (Networking.IsOwner(gameObject) && value)
            {
                rbPosition = rb.position;
                rbRotation = rb.rotation.eulerAngles;
                rbVelocity = rb.velocity;
                rbAngular = rb.angularVelocity;
                syncRigidbody = false;
            }
            else if (!Networking.IsOwner(gameObject) && !value)
            {
                rb.position = rbPosition;
                rb.rotation = Quaternion.Euler(rbRotation);
                rb.velocity = rbVelocity;
                rb.angularVelocity = rbAngular;
            }
            RequestSerialization();
        }
        get => syncRigidbody;
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
                Debug.Log("Player Held Reset");
                //Set Kinematic back to default
                rb.isKinematic = kinematicDefault;
                rb.useGravity = gravityDefault;
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
            Debug.Log("PlayerBone Changed to: " + value);
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
        gravityDefault = rb.useGravity;

        //Saftey Check
        if (pickup.ExactGrip == transform)
            pickup.ExactGrip = null;

        if (pickup.ExactGun == transform)
            pickup.ExactGun = null;
    }

    private void Update()
    {
        /*
        //TODO can we move this to Field Callback?
        if (pickup.pickupable)
        {
            //If we don't own it and someone else has it
            if (!Networking.IsOwner(gameObject) && HasPlayer())
            {
                pickup.pickupable = false;
            }
        }
        else
        {
            if (Networking.IsOwner(gameObject) || !HasPlayer())
            {
                pickup.pickupable = true;
            }
        }
        */
    }

    //Only happens on OWNER
    public override void OnPickup()
    {
        Debug.Log("OnPickup - Owner Only");

        //New Ownership
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);

        Debug.Log("Pickup Pre Values - ID: " + PlayerId + " Bone: " + PlayerBone);
        PlayerId = Networking.LocalPlayer.playerId;
        PlayerBone = (int)PickupHandToHumanBone(pickup.currentHand);
        Debug.Log("Pickup POST Values - ID: " + PlayerId + " Bone: " + PlayerBone);

        //Local Update Time - Set to 5 sec to compenstate for Delay/Lag/Update 
        intialUpdateTime = 5f;

        RequestSerialization();
    }

    private void FixedUpdate()
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
            if (intialUpdateTime > 0)
            {
                intialUpdateTime -= Time.deltaTime;

                if (pickup.ExactGun != null)
                {
                    OffsetRotation = Quaternion.Inverse(GetBoneRotation()) * pickup.ExactGun.rotation;
                    //OffsetPosition = GetBonePosition() - rb.position;
                }
                else if (pickup.ExactGrip != null)
                {
                    OffsetRotation = Quaternion.Inverse(GetBoneRotation()) * pickup.ExactGrip.rotation;
                    //OffsetPosition = pickup.ExactGrip.position - GetBonePosition();
                }
            }
            return;
        }

        //If pickup is being held and the player is in game
        if (HasPlayer())
        {
            //Set to Kinematic on Clients
            if (rb.isKinematic != true)
                rb.isKinematic = true;
            //Disable Gravity on Clients
            if (rb.useGravity != false)
                rb.useGravity = false;



            //Update Location
            if (pickup.orientation == VRC_Pickup.PickupOrientation.Gun)
            {
                //Gun
                rb.MoveRotation((GetBoneRotation() * OffsetRotation) * Quaternion.Euler(gunRotationOffset));
                rb.MovePosition(GetBonePosition() + GetBoneRotation() * gunOffset);

            }
            else if (pickup.orientation == VRC_Pickup.PickupOrientation.Grip)
            {
                //Grip
                rb.MoveRotation((GetBoneRotation() * OffsetRotation) * Quaternion.Euler(gripRotationOffset));
                rb.MovePosition(GetBonePosition() + GetBoneRotation() * gripOffset);
            }                               
            else
            {
                //Default - Tracking
                rb.MovePosition(GetBonePosition());
                rb.MoveRotation(OffsetRotation);
            }

        }
        else if (PlayerId >= 0 && VRCPlayerApi.GetPlayerById(PlayerId) == null)
            OnDrop();
    }


    //Only Happens on OWNER
    public override void OnDrop()
    {
        Debug.Log("OnDrop - Owner Only");

        //Set Kinematic back to default
        rb.isKinematic = kinematicDefault;
        rb.useGravity = gravityDefault;

        //Last Ownership reset
        if (Networking.IsOwner(gameObject))
        {
            Debug.Log("Drop PRE Values - ID: " + PlayerId + " Bone: " + PlayerBone);

            //Default Values
            PlayerId = -1;
            PlayerBone = -1;
            OffsetRotation = Quaternion.identity;

            Debug.Log("Drop POST Values - ID: " + PlayerId + " Bone: " + PlayerBone);
            //Sync velocity and anglar Velocity TODO reenable later
            //SyncRigidbody = true;
        }
    }

    /*
    private void CalculateOffsets(HumanBodyBones bone)
    {
        Vector3 objPos = rb.transform.position;
        Vector3 plyPos = Networking.LocalPlayer.GetBonePosition(bone);
        Quaternion invRot = Quaternion.Inverse(Networking.LocalPlayer.GetBoneRotation(bone));
        // q^(-1) * Vector (x2-x1, y2-y1, z2-z1)
        objectPosOffset = invRot * (objPos - plyPos);
        // calculate the rotation by multiplying the current rotation with inverse player rotation
        objectRotOffset = invRot * rb.transform.rotation;
    }
    */

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

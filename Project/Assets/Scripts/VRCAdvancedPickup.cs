using UdonSharp;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual), RequireComponent(typeof(Rigidbody),typeof(VRCPickup))]
public class VRCAdvancedPickup : UdonSharpBehaviour
{
    [Header("Networked")]
    [SerializeField, UdonSynced, FieldChangeCallback(nameof(PlayerId))]
    private int playerId = -1;
    [SerializeField, UdonSynced, FieldChangeCallback(nameof(PlayerHand))]
    private int playerHand = 1;

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

    [SerializeField, UdonSynced]
    private float testByte;


    //Local
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


    public int PlayerId
    {
        set
        {
            playerId = value;
            if (playerId == -1)
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

            RequestSerialization();
        }
        get => playerId;
    }
    public int PlayerHand
    {
        set
        {
            playerHand = value;
            RequestSerialization();
        }
        get => playerHand;
    }

    private VRCPlayerApi.TrackingData data;

    private void Start()
    {
        pickup = GetComponent<VRC_Pickup>();

        if (!rb)
            rb = GetComponent<Rigidbody>();

        kinematicDefault = rb.isKinematic;
        gravityDefault = rb.useGravity;
    }

    private void FixedUpdate()
    {
        //No Network player
        if (Networking.LocalPlayer == null)
            return;

        //Make sure the right hand is in use
        if (Networking.IsOwner(gameObject) && PlayerHand != (int)pickup.currentHand)
            PlayerHand = (int)pickup.currentHand;

        //Let Pickup handle local player
        if (PlayerId == Networking.LocalPlayer.playerId)
            return;

        if(PlayerId != Networking.LocalPlayer.playerId)

        //If pickup is being held and the player is in game
        if (PlayerId >= 0 && VRCPlayerApi.GetPlayerById(PlayerId) != null)
        {
            data = VRCPlayerApi.GetPlayerById(PlayerId).GetTrackingData((VRCPlayerApi.TrackingDataType)PlayerHand);

            //VRCPlayerApi.TrackingDataType.RightHand;
            //VRCPickup.PickupHand.Right;

            //Set to Kinematic on Clients
            if (!rb.isKinematic)
                rb.isKinematic = true;
            //Disable Gravity on Clients
            if (rb.useGravity)
                gravityDefault = false;

            //Update Location
            if (pickup.orientation == VRC_Pickup.PickupOrientation.Gun)
            {
                //Gun
                rb.MovePosition(VRCPlayerApi.GetPlayerById(PlayerId).GetBonePosition(HumanBodyBones.RightHand) + pickup.ExactGun.localPosition);
                //rb.MovePosition(data.position + pickup.ExactGun.localPosition);
                rb.MoveRotation(data.rotation * pickup.ExactGun.localRotation);
            }
            else if (pickup.orientation == VRC_Pickup.PickupOrientation.Grip)
            {
                //Grip
                rb.MovePosition(data.position + pickup.ExactGrip.localPosition);
                rb.MoveRotation(Quaternion.Euler(data.rotation.eulerAngles + pickup.ExactGrip.localRotation.eulerAngles));
            }
            else
            {
                //Default
                rb.MovePosition(data.position);
                rb.MoveRotation(data.rotation);
            }

        }
        else if(PlayerId >= 0 && VRCPlayerApi.GetPlayerById(PlayerId) == null)
            OnDrop();
    }

    public override void OnPickup()
    {
        Debug.Log("OnPickup");
        //Already holding? Drop it
        if (PlayerId == Networking.LocalPlayer.playerId)
        {
            OnDrop();
            return;
        }
        else if (pickup.DisallowTheft && PlayerId >= 0)
        {
            Debug.Log("THEFT");
            OnDrop();
            //Cannot grab something held by someone else
            return;
        }

        //Passed Checks, give ownership
        HoldPickup();

    }

    void Drop()
    {
        pickup.Drop();
    }

    public override void OnDrop()
    {
        Debug.Log("OnDrop");

        if (Networking.IsOwner(gameObject))
        {
            PlayerId = -1;
            //Sync velocity and anglar Velocity
            SyncRigidbody = true;
            RequestSerialization();
        }
        else
        {
            //Set Kinematic back to default
            rb.isKinematic = kinematicDefault;
            rb.useGravity = gravityDefault;
        }
    }


    void HoldPickup()
    {
        Debug.Log("Hold Pickup!");
        if (!Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }
        PlayerId = Networking.LocalPlayer.playerId;
        PlayerHand = (int)pickup.currentHand;
        RequestSerialization();
    }
}

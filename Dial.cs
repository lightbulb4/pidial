using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

// optimized, networked, and modified by lightbulb

// original code from pi under the following license
// All assets in this repository are licensed under the terms of 'CC BY-NC-SA 2.0' unless explicitly otherwise marked.
// For exceptions contact me directly (see https://pimaker.at or Discord pi#4219).
// You can view a full copy of the license here: https://creativecommons.org/licenses/by-nc-sa/2.0/

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class Dial : UdonSharpBehaviour
{
    [Header("Dial Behaviour")]

    [UdonSynced]
    public int syncedState = 0;

    public int CurrentState = 0;

    public int SuspendedState = -1;

    public AudioLinkMaterialProp audioLinkMaterialProp;

    [Header("Internal Settings")]

    public GameObject RotatorVR, RotatorDesktop;
    public GameObject RotatorVRMesh;
    private GameObject activeObj;

    private Vector3 baseRotation;
    private Vector3 basePos;

    private const int BOUND_NONE = 0, BOUND_LEFT = 1, BOUND_RIGHT = 2;
    private int boundHand;
    private VRCPlayerApi.TrackingData boundBase;
    private float boundStartRotation;

    private bool initVR;

    private int suspendedFrom = -1;

    void Start()
    {
        // always start out in Desktop mode, IsUserInVR() doesn't
        // work in Start() - wait a bit and try again in Update()
        this.initVR = false;
        RotatorDesktop.SetActive(true);
        RotatorVR.SetActive(false);
        this.activeObj = RotatorDesktop;

        this.CurrentState = Mathf.Clamp(this.CurrentState, 0, 3);

        this.baseRotation = this.activeObj.transform.localRotation.eulerAngles;
        this.basePos = this.activeObj.transform.localPosition;
        this.boundHand = 0;

        var collider = this.GetComponent<BoxCollider>();
        if (this.SuspendedState != -1 && collider != null && !collider.bounds.Contains(Networking.LocalPlayer.GetPosition()))
        {
            // outside collider, start suspended
            var tmp = this.CurrentState;
            this.SetState(this.SuspendedState, false);
            this.suspendedFrom = tmp;
        }
        else
        {
            SetState(this.CurrentState, false);
        }
    }

    public int NextState;

    private void SetState(int state, bool transition)
    {
        if (this.suspendedFrom != -1 || state < 0)
        {
            // we're suspended, ignore
            return;
        }

        this.CurrentState = state;

        // if transition is set, also perform actions,
        // otherwise the rotation is just visual
        if (transition)
        {
            audioLinkMaterialProp.band = state;
        }

        // Set dial to correct rotation (fix it to its "slot")
        this.activeObj.transform.localRotation =
            Quaternion.Euler(this.baseRotation.x, this.baseRotation.y + 90f * state,
                this.baseRotation.z);

        RotatorVRMesh.transform.localRotation = Quaternion.Euler(this.baseRotation.x, this.baseRotation.y + 90f * state,
                this.baseRotation.z);
    }

    public override void OnDeserialization()
    {
        if(CurrentState != syncedState)
        {
            CurrentState = syncedState;
            SetState(syncedState, true);
        }
    }

    // called from DialDesktopInteractor
    public void DesktopInteract()
    {
        // Called on interact with the desktop dial, simply go to next state with rollover
        SetState(this.CurrentState == 3 ? 0 : this.CurrentState + 1, true);
    }

    public void VRInteractStart()
    {
        // User has picked up the dial in VR, see which hand it is in
        var leftPickup = Networking.LocalPlayer.GetPickupInHand(VRC_Pickup.PickupHand.Left);
        var rightPickup = Networking.LocalPlayer.GetPickupInHand(VRC_Pickup.PickupHand.Right);
        this.boundHand = leftPickup != null && leftPickup.gameObject == this.RotatorVR.gameObject ?
            BOUND_LEFT : (rightPickup != null && rightPickup.gameObject == this.RotatorVR.gameObject ?
                BOUND_RIGHT : BOUND_NONE);

        // this is the base transform right now, i.e. what we rotate off later
        this.boundBase = Networking.LocalPlayer.GetTrackingData(
            this.boundHand == BOUND_RIGHT ?
            VRCPlayerApi.TrackingDataType.RightHand :
            VRCPlayerApi.TrackingDataType.LeftHand);

        this.boundStartRotation = this.activeObj.transform.localRotation.eulerAngles.y;
    }

    public void VRInteractEnd()
    {
        // player let go, get current rotation to determine state
        var rotation = this.activeObj.transform.localRotation.eulerAngles.y;

        // one last position reset for good measure
        this.RotatorVR.transform.localPosition = this.basePos;

        // normalize rotation
        rotation %= 360;
        if (rotation < 0)
        {
            rotation += 360;
        }

        this.boundHand = BOUND_NONE;

        if (!Networking.IsOwner(this.gameObject))
        {
            Networking.SetOwner(Networking.LocalPlayer, this.gameObject);
        }
        
        // select state based on rotation
        if (rotation < 45 || rotation >= 315)
        {
            syncedState = 0;
            SetState(0, true);
        }
        else if (rotation < 135)
        {
            syncedState = 1;
            SetState(1, true);
        }
        else if (rotation < 225)
        {
            syncedState = 2;
            SetState(2, true);
        }
        else if (rotation < 315)
        {
            syncedState = 3;
            SetState(3, true);
        }

        RequestSerialization();
    }

    void Update()
    {
        // Detect VR properly now
        if (!this.initVR && Networking.LocalPlayer.IsUserInVR())
        {
            Debug.Log("[Dial] User is in VR!");
            this.initVR = true;
            RotatorDesktop.SetActive(false);
            RotatorVR.SetActive(true);
            RotatorVRMesh.SetActive(true);
            this.activeObj = RotatorVR;
            SetState(this.CurrentState, false);
        }

        if (this.initVR && boundHand != BOUND_NONE)
        {
            // player has grabbed knob, do VR rotation tracking
            var trackState = Networking.LocalPlayer.GetTrackingData(
                this.boundHand == BOUND_RIGHT ?
                VRCPlayerApi.TrackingDataType.RightHand :
                VRCPlayerApi.TrackingDataType.LeftHand);

            // now calculate hand rotation since start of grab
            // god I despi^Wlove math sometimes

            // this is actually "forward", i.e. where your extended finger points
            // var baseForward = this.boundBase.rotation * Vector3.left;
            // nevermind, I've decided that it feels better if the "plane" you rotate
            // on is actually the rotation of the object, not your initial hand position
            var baseForward = this.gameObject.transform.up;

            // these point "down" through your fist
            var baseUp = this.boundBase.rotation * Vector3.up;
            var trackUp = trackState.rotation * Vector3.up;

            // project both on an imaginary plane so we only get one axis of rotation
            var projBase = Vector3.ProjectOnPlane(baseUp, baseForward);
            var projTrack = Vector3.ProjectOnPlane(trackUp, baseForward);

            // get the angle on the "2d plane" where we projected the vectors onto
            var trackAngle = Vector3.SignedAngle(projBase, projTrack, baseForward);

            // apply rotation to dial rotator
            var newRotation = this.baseRotation.y + this.boundStartRotation + trackAngle;
            this.activeObj.transform.localRotation = Quaternion.Euler(
                this.baseRotation.x,
                newRotation,
                this.baseRotation.z);

            // if we separate the mesh & the collider, this no longer allows the "master"
            // to yoink the mesh away from its 3d space. but rather they can hold the invisible
            // collider anywhere they want, but the only rotation is applied to the mesh.
            // this allows a network synced mesh position, without it being out of place while being held
            RotatorVRMesh.transform.localRotation = Quaternion.Euler(
                this.baseRotation.x,
                newRotation,
                this.baseRotation.z);


            // don't allow breaking off the dial, i.e. keep it in place
            // lb: this is setting the mesh of the dial, to the position of the parent
            this.activeObj.transform.localPosition = this.basePos;
        }
    }

    //public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    //{
    //    if (player.isLocal)
    //    {
    //        var tmp = this.suspendedFrom;
    //        this.suspendedFrom = -1;
    //        this.SetState(tmp, true);
    //    }
    //}

    //public override void OnPlayerTriggerExit(VRCPlayerApi player)
    //{
    //    if (player.isLocal && this.SuspendedState != -1)
    //    {
    //        var tmp = this.CurrentState;
    //        this.SetState(this.SuspendedState, true);
    //        this.suspendedFrom = tmp;
    //    }
    //}
}
using System.Collections.Generic;
using System.Net;
using Robot;
using Robot.Conf;
// using Unity.XR.PICO.TOBSupport;
// using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;

public class UIOperate : MonoBehaviour
{
    public Text SN;
    public Text LocalIP;
    public Text TargetIP;
    public Text TrackNum;
    public Toggle HeadTog;
    public Toggle ControllerTog;
    public Toggle HandTrackingTog;
    public Toggle SendTog;
    public Toggle AcontrolerTog;
    public Dropdown bodyModeDrop;
    public TcpHandler TcpHandler;
    public Text BodyInfo;
    public Toggle HighAccuracy;
    public Text Version;
    public Button ReconnectBtn;
    public Toggle NetshareTog;

    public GameObject Simulator;
    public GameObject CameraObj;
    public GameObject IpInputDialog;
    public GameObject ExtDevPanel;
    public InputActionProperty SendDataAction;

    [Space(30)][Header("Refactoring")] public VideoSourceManager videoSource;
    public VideoSourceConfigManager sourceConfig => videoSource.videoSourceConfigManager;

    public Dropdown videoSourceDropdown;

    // Start is called before the first frame update
    private void Awake()
    {
#if UNITY_EDITOR
        if (Simulator != null)
        {
            Simulator.SetActive(true);
        }
#endif
        // ReconnectBtn.gameObject.SetActive(false);

        bodyModeDrop.onValueChanged.AddListener(OnBodyModeDrop);
        HeadTog.onValueChanged.AddListener(OnHeadTog);
        ControllerTog.onValueChanged.AddListener(OnControllerTog);
        HandTrackingTog.onValueChanged.AddListener(OnHandTrackingTog);

        SendTog.onValueChanged.AddListener(OnSendTog);
        Version.text = "v: " + Application.version;
        // Meta IOBT has no separate high-accuracy runtime switch: fidelity is a project setting
        // (OVRRuntimeSettings.BodyTrackingFidelity), not a per-session call like PICO's
        // StartBodyTracking(mode). The toggle stays hidden so it cannot imply an inactive control.
        HighAccuracy.gameObject.SetActive(false);
        NetshareTog.onValueChanged.AddListener(OnNetShareTog);
        ReconnectBtn.onClick.AddListener(OnReconnectBtn);
        //The shared network function is only available on B-end devices.
        NetshareTog.gameObject.SetActive(false);
        // Bypass getting sn via enterprise service to enable data transport
        SetDeviceSN("TestDevice");
        // bool intEnterprise = PXR_Enterprise.InitEnterpriseService();
        // Debug.Log("---InitEnterpriseService :" + intEnterprise);
        // PXR_Enterprise.BindEnterpriseService(OnBindEnterpriseService);

        // if (CameraObj != null)
        // {
        //     CameraObj.SetActive(false);
        // }

        AndroidProxy.CallBack += OnAndroidCallBack;
#if UNITY_EDITOR
        SetDeviceSN("TestDevice");
#endif
        // Refactoring
        sourceConfig.OnInitialized += OnSourceConfigOnOnInitialized;
        // Initialize video source configuration
        sourceConfig.Initialize();

        // Enable SendDataAction for Quest controllers
        if (SendDataAction != null)
        {
            SendDataAction.action.Enable();
            Debug.Log("SendDataAction enabled successfully");
        }
        else
        {
            Debug.LogWarning("SendDataAction.action is null - input action not properly configured");
        }
        
        // set FPS to 90
        OVRPlugin.systemDisplayFrequency = 90.0f;
    }

    private void OnSourceConfigOnOnInitialized()
    {
        // Update videoSourceDropdown options
        print("OnSourceConfigOnOnInitialized");
        videoSourceDropdown.ClearOptions();
        videoSourceDropdown.AddOptions(sourceConfig.GetVideoSourceNames());
    }

    private void OnAndroidCallBack(string key, string value)
    {
        if (key == "RequestPermissionsBack")
        {
            if (value == "0")
            {
                if (CameraObj != null)
                {
                    CameraObj.SetActive(true);
                }
            }
            else
            {
                Toast.Show("Permission denied!");
            }
        }
    }

    private void OnReconnectBtn()
    {
        TcpHandler.Reconnect();
    }

    public void TcpConnect(string ip)
    {
        TargetIP.text = "PC Service: " + ip;
        ReconnectBtn.gameObject.SetActive(true);
        TcpHandler.Connect(ip);
        ConnectSuccess();
    }

    public void ConnectSuccess()
    {
        TargetIP.text = "PC Service: " + TcpHandler.GetTargetIP;
    }

    private void OnBindEnterpriseService(bool bind)
    {
        Debug.Log("OnBindEnterpriseService " + bind);
        if (bind)
        {
            //The shared network function is only available on B-end devices.
            NetshareTog.gameObject.SetActive(true);
            // PXR_Enterprise.GetSwitchSystemFunctionStatus(SystemFunctionSwitchEnum.SFS_USB_TETHERING,
            //     (value) => { NetshareTog.SetIsOnWithoutNotify(value == 1); });
            //
            // string sn = PXR_Enterprise.StateGetDeviceInfo(SystemInfoEnum.EQUIPMENT_SN);
            // SetDeviceSN(sn);
        }
    }

    private void SetDeviceSN(string sn)
    {
        TcpHandler.SetDeviceSn(sn);
        Debug.Log("SN: " + sn);
        SN.text = "SN: " + sn;
    }

    private void OnNetShareTog(bool ison)
    {
        //     Debug.Log("OnNetShareTog:" + ison);
        //     if (ison)
        //         PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_USB_TETHERING, SwitchEnum.S_ON);
        //     else
        //         PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_USB_TETHERING, SwitchEnum.S_OFF);
        //
        //     PXR_Enterprise.GetSwitchSystemFunctionStatus(SystemFunctionSwitchEnum.SFS_USB_TETHERING,
        //         (value) => { Debug.Log("SFS_USB_TETHERING:" + value); });
    }

    public void OnQuit()
    {
        Application.Quit();
    }

    public void OnExtraDevBtn()
    {
        ExtDevPanel.SetActive(true);
    }

    public void OnWriteIpBtn()
    {
        IpInputDialog.SetActive(true);
    }

    private void OnBodyModeDrop(int index)
    {
        TrackingData.TrackingType tType = (TrackingData.TrackingType)bodyModeDrop.value;

        // Quest counterpart of PICO's tracker mode/number check. There is nothing to calibrate or
        // pair here (IOBT runs off the headset cameras), so the only precondition is that the
        // runtime reports support. Reject up front rather than letting the dropdown sit on a mode
        // that will never produce data.
        if (tType == TrackingData.TrackingType.Body && !OVRPlugin.bodyTrackingSupported)
        {
            bodyModeDrop.SetValueWithoutNotify(0);
            RefreshBodyInfo();
            return;
        }

        UpdateBodyTracking();
    }


    public void OnOpenCameraOperate()
    {
        if (CameraObj != null)
        {
            if (Permission.HasUserAuthorizedPermission(Permission.Camera) &&
                Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                CameraObj.SetActive(!CameraObj.activeSelf);
            }
            else if (!CameraObj.activeSelf)
            {
                var permissionCallbacks = new PermissionCallbacks();
                permissionCallbacks.PermissionGranted += PermissionGranted;
                permissionCallbacks.PermissionDenied += PermissionDenied;

                string[] permissions = { Permission.Camera, Permission.Microphone };
                Permission.RequestUserPermissions(permissions, permissionCallbacks);
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageRead))
            {
                Permission.RequestUserPermission(Permission.ExternalStorageRead);
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
            {
                Permission.RequestUserPermission(Permission.ExternalStorageWrite);
            }
        }
    }

    private void PermissionDenied(string obj)
    {
        Toast.Show("Permission denied!");
    }

    private void PermissionGranted(string obj)
    {
        if (CameraObj != null)
        {
            CameraObj.SetActive(true);
        }
    }

    private void RefreshLocalIP()
    {
        string localIP = Utils.GetLocalIPv4();
        LocalIP.text = localIP;
    }

    // Obtain the local IPv6 address
    private string GetLocalIPv6()
    {
        string localIP = "Not found";
        foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                localIP = ip.ToString();
                break;
            }
        }

        return localIP;
    }


    private void OnHeadTog(bool on)
    {
        TrackingData.SetHeadOn(on);
    }

    private void OnControllerTog(bool on)
    {
        TrackingData.SetControllerOn(on);

        // Disable on quest for now
        // // Ensure mutual exclusivity with HandTrackingTog
        // if (on && HandTrackingTog.isOn)
        // {
        //     HandTrackingTog.SetIsOnWithoutNotify(false);
        //     TrackingData.SetHandTrackingOn(false);
        // }
    }

    private void OnHandTrackingTog(bool on)
    {
        TrackingData.SetHandTrackingOn(on);

        // Disable on quest for now
        // // Ensure mutual exclusivity with ControllerTog
        // if (on && ControllerTog.isOn)
        // {
        //     ControllerTog.SetIsOnWithoutNotify(false);
        //     TrackingData.SetControllerOn(false);
        // }
    }

    private void OnSendTog(bool on)
    {
        TcpHandler.SendTrackingData = on;
        // Reset FPS
        if (!on)
        {
            FPSDisplay.Reset();
        }
    }


    private void UpdateBodyTracking()
    {
        TrackingData.TrackingType tType = (TrackingData.TrackingType)bodyModeDrop.value;
        Debug.Log("UpdateBodyTracking " + tType);

        // Quest has no external trackers; IOBT is inferred from the headset cameras alone.
        TrackNum.text = "";

        if (tType == TrackingData.TrackingType.Motion)
        {
            // PICO's motion-tracker mode has no Quest counterpart; reject rather than silently
            // publish nothing, otherwise the dropdown looks functional but sends no data.
            bodyModeDrop.SetValueWithoutNotify(0);
            tType = TrackingData.TrackingType.None;
            _rejectedMotionUntil = Time.time + 3f;
        }

        TrackingData.SetTrackingType(tType);
        RefreshBodyInfo();
    }

    /// <summary>
    /// Until when the "PICO-only" rejection notice stays on screen, as Time.time.
    /// </summary>
    /// <remarks>
    /// RefreshBodyInfo runs every frame and would otherwise overwrite the notice before it could
    /// be read: the dropdown is snapped back to Off, so the very next frame reports "closed".
    /// </remarks>
    private float _rejectedMotionUntil;

    /// <summary>
    /// Writes the live body tracking status line.
    /// </summary>
    /// <remarks>
    /// Called every frame rather than only on dropdown change. Calibration is the reason: it
    /// starts out Calibrating and reaches Valid tens of seconds later, and joint scale is not
    /// trustworthy until it does. A status line written once at selection time can never show
    /// that transition, so the operator has no way to tell when the data became usable -- which
    /// is exactly the question they have while standing there wearing the headset.
    /// </remarks>
    private void RefreshBodyInfo()
    {
        if (Time.time < _rejectedMotionUntil)
        {
            BodyInfo.color = Color.red;
            BodyInfo.text = "Motion tracker mode is PICO-only";
            return;
        }

        if (TrackingData.TrackingTypeValue != TrackingData.TrackingType.Body)
        {
            BodyInfo.color = Color.white;
            BodyInfo.text = "Body tracking off";
            return;
        }

        // Meta needs no Start/Stop call: OVRBody requests the tracking session on enable and the
        // runtime keeps delivering BodyState. Selecting Body is purely a publish decision, so the
        // status line reports whether the runtime can actually serve us.
        if (!OVRPlugin.bodyTrackingSupported)
        {
            BodyInfo.color = Color.red;
            BodyInfo.text = "Body tracking unsupported on this device";
            return;
        }

        if (!OVRPlugin.bodyTrackingEnabled)
        {
            // The system-level toggle is the usual culprit and cannot be changed from here.
            BodyInfo.color = Color.red;
            BodyInfo.text = "Enable Settings > Movement Tracking > Body Tracking";
            return;
        }

        var source = TrackingData.SharedQuestTrackingDataSource;
        var state = source != null ? source.GetBodyState() : null;
        if (state == null || state.Value.JointLocations == null)
        {
            // Normal whenever the headset is off the head, neck included: the system's mount
            // detection gates body tracking, so hanging it round the neck stops the data even
            // though controller teleop keeps working. See IsBodyTrackingActive for the evidence.
            BodyInfo.color = Color.yellow;
            BodyInfo.text = "No body data - put the headset on";
            return;
        }

        var body = state.Value;
        // Green once calibration reads Valid, but this is a readout and not a gate: joints are
        // published throughout, the same way Unity-Movement's sample scene consumes them. Calibration
        // runs inside the runtime with no way to switch it off, so waiting for green is optional --
        // it only means the runtime has stopped adjusting the skeleton's scale.
        var calibrated = body.CalibrationStatus == OVRPlugin.BodyTrackingCalibrationState.Valid;
        BodyInfo.color = calibrated ? Color.green : Color.yellow;
        // Fidelity is omitted: it is fixed at build time (OVRRuntimeSettings), so showing it every
        // frame spends width on a constant. Calibration and confidence are the two that move.
        BodyInfo.text = $"Joints: {body.JointLocations.Length}   " +
                        $"Calibration: {body.CalibrationStatus}   " +
                        $"Confidence: {body.Confidence:F2}";
    }

    private float _lastTime = 0;
    private float _lastBodyInfoRefresh = 0;

    // Update is called once per frame
    void Update()
    {
        if (TcpHandler.State != SocketState.WORKING)
        {
            if (Time.time - _lastTime > 2)
            {
                _lastTime = Time.time;
                RefreshLocalIP();
            }
        }

        // Throttled: calibration state changes over tens of seconds, so rewriting the string at
        // the 90 Hz frame rate would only cost a Text layout rebuild per frame for no gain.
        if (Time.time - _lastBodyInfoRefresh > 0.2f)
        {
            _lastBodyInfoRefresh = Time.time;
            RefreshBodyInfo();
        }

        if (AcontrolerTog != null && AcontrolerTog.isOn)
        {
            // Use Input Actions only
            if (SendDataAction != null && SendDataAction.action.WasReleasedThisFrame())
            {
                SendTog.isOn = !SendTog.isOn;
                LogWindow.Info("Sending data: " + SendTog.isOn);
                Debug.Log("SendDataAction triggered - Sending data: " + SendTog.isOn);
            }
        }
    }

    public void OnQuitBtn()
    {
        Application.Quit();
    }

    private void OnDestroy()
    {
        // Disable SendDataAction when object is destroyed
        if (SendDataAction != null)
        {
            SendDataAction.action.Disable();
        }
    }
}
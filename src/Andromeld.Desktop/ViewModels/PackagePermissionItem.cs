namespace Andromeld.Desktop.ViewModels;

public sealed record PackagePermissionItem(string Id)
{
    public string Name => Id switch
    {
        "android.permission.CAMERA" => "相机",
        "android.permission.RECORD_AUDIO" => "麦克风",
        "android.permission.POST_NOTIFICATIONS" => "通知",
        "android.permission.READ_MEDIA_IMAGES" => "读取图片",
        "android.permission.READ_MEDIA_VIDEO" => "读取视频",
        "android.permission.READ_MEDIA_AUDIO" => "读取音频",
        "android.permission.BLUETOOTH_CONNECT" => "蓝牙连接",
        "android.permission.INTERNET" => "访问网络",
        "android.permission.ACCESS_NETWORK_STATE" => "查看网络状态",
        "android.permission.ACCESS_FINE_LOCATION" => "精确位置",
        "android.permission.ACCESS_COARSE_LOCATION" => "大致位置",
        "android.permission.READ_CONTACTS" => "读取联系人",
        "android.permission.READ_EXTERNAL_STORAGE" => "读取存储空间",
        "android.permission.WRITE_EXTERNAL_STORAGE" => "写入存储空间",
        _ => Id[(Id.LastIndexOf('.') + 1)..]
    };
}

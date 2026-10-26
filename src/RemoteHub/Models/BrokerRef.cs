using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Models;

/// <summary>What an item of a synced RemoteHub Broker folder stands for.</summary>
public enum BrokerItemKind
{
    /// <summary>The folder for one broker organisation, at the top of the navigation.</summary>
    Root,
    /// <summary>Where devices that registered themselves land until a console places them.</summary>
    NewDevices,
    /// <summary>A folder of the organisation's shared layout.</summary>
    Folder,
    /// <summary>A device registered with the broker.</summary>
    Device,
}

/// <summary>
/// Set on the folders and connections that mirror a broker organisation's shared list. The structure comes from the
/// broker (sealed); credentials, RDP options and notes on these nodes stay this console's own.
/// </summary>
public sealed partial class BrokerRef : ObservableObject
{
    /// <summary>This PC's broker connection (<see cref="Broker.BrokerConnection.Id"/>).</summary>
    public string Connection { get; set; } = "";
    public BrokerItemKind Kind { get; set; }
    /// <summary>The broker's id: the folder's or the device's (empty for the root and New devices).</summary>
    public string Item { get; set; } = "";
    /// <summary>A device: the device secret it registered with – its offers are sealed with that secret's record key.</summary>
    public string? SecretId { get; set; }
    /// <summary>The highest version of this folder's entry or this device's placement seen, so an older one is ignored.</summary>
    public long Version { get; set; }
    /// <summary>A device's record checked against the organisation's record keys; unsealed devices can't be opened.</summary>
    public bool Sealed { get; set; } = true;

    /// <summary>Online at the broker (pushed live); null = not known yet.</summary>
    [ObservableProperty] [property: JsonIgnore] private bool? _online;
    /// <summary>This PC is connected to that broker (false on a PC that shares the settings but isn't connected).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _connected;

    public BrokerRef Clone() => (BrokerRef)MemberwiseClone();
}

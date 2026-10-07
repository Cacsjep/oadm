# network-settings (Version 2.0.3)

## Overview

- **Short Description**: Network configuration service.
- **ID**: network-settings
- **Version**: 2.0.3
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
network-settings.v2 (Root Entity)
    ├── modem_supported (Property)
    ├── sfp_supported (Property)
    ├── switch_supported (Property)
    ├── wired_supported (Property)
    ├── wlan_supported (Property)
    ├── ip (Entity)
        ├── ipStatus (Property)
        ├── configs (Entity Collection)
            ├── id (Property)
            ├── ipv4 (Property)
            ├── ipv6 (Property)
    ├── modem (Entity)
        ├── imei (Property)
        ├── sim (Entity Collection)
            ├── iccid (Property)
            ├── pinRetries (Property)
            ├── provider (Property)
            ├── pukRetries (Property)
            ├── slotId (Property)
            ├── status (Property)
            ├── changeSimPin (Action)
            ├── setSimProtection (Action)
            ├── unblockSim (Action)
            ├── verifySimPin (Action)
            ├── connection (Entity)
                ├── connectionStatus (Property)
                ├── operatorName (Property)
                ├── radioInterface (Property)
                ├── getRadioParameters (Action)
                ├── networkInterface (Entity Collection)
                    ├── accessPointName (Property)
                    ├── interfaceName (Property)
                    ├── ipv4Info (Property)
                    ├── setAccessPointName (Action)
    ├── sfp (Entity)
        ├── ipConfigId (Property)
        ├── linkMode (Property)
        ├── lowerState (Property)
        ├── macAddress (Property)
        ├── maxMtu (Property)
        ├── maxNumberOfVlans (Property)
        ├── minMtu (Property)
        ├── mtu (Property)
        ├── staticLinkMode (Property)
        ├── staticMtu (Property)
        ├── supportedLinkModes (Property)
        ├── auth (Entity)
            ├── macsecStatus (Property)
            ├── mode (Property)
            ├── status (Property)
            ├── configs (Entity)
                ├── eapTls (Property)
                ├── macsecPsk (Property)
                ├── mschapv2 (Property)
        ├── vlans (Entity Collection)
            ├── id (Property)
            ├── ipConfigId (Property)
            ├── ipStatus (Property)
            ├── state (Property)
    ├── switch (Entity)
        ├── port (Entity Collection)
            ├── enabled (Property)
            ├── lowerState (Property)
            ├── portId (Property)
            ├── remoteAddresses (Property)
            ├── security_supported (Property)
            ├── security (Entity)
                ├── authServerEnabled (Property)
                ├── authServerEnforced (Property)
                ├── authState (Property)
                ├── macSecState (Property)
    ├── system (Entity)
        ├── activeUplink (Property)
        ├── hostname (Property)
        ├── nameServers (Property)
        ├── searchDomains (Property)
        ├── staticHostname (Property)
        ├── staticNameServers (Property)
        ├── staticSearchDomains (Property)
        ├── tcpEcn (Property)
        ├── uplinkBandwidthLimitation (Property)
        ├── useDhcpHostname (Property)
        ├── useDhcpResolver (Property)
    ├── wired (Entity)
        ├── ipConfigId (Property)
        ├── linkMode (Property)
        ├── lowerState (Property)
        ├── macAddress (Property)
        ├── managesSfp (Property)
        ├── maxMtu (Property)
        ├── maxNumberOfVlans (Property)
        ├── minMtu (Property)
        ├── mtu (Property)
        ├── staticLinkMode (Property)
        ├── staticMtu (Property)
        ├── supportedLinkModes (Property)
        ├── auth (Entity)
            ├── macsecStatus (Property)
            ├── mode (Property)
            ├── status (Property)
            ├── configs (Entity)
                ├── eapTls (Property)
                ├── macsecPsk (Property)
                ├── mschapv2 (Property)
        ├── vlans (Entity Collection)
            ├── id (Property)
            ├── ipConfigId (Property)
            ├── ipStatus (Property)
            ├── state (Property)
    ├── wlan (Entity)
        ├── ap_supported (Property)
        ├── countryCode (Property)
        ├── countryCodeLocked (Property)
        ├── station_supported (Property)
        ├── ap (Entity)
            ├── authMode (Property)
            ├── enabled (Property)
            ├── setupServer_supported (Property)
            ├── ssid (Property)
            ├── setupServer (Entity)
                ├── switchApToStation (Action)
        ├── station (Entity)
            ├── authState (Property)
            ├── enabled (Property)
            ├── maxNumberOfAuthConfigs (Property)
            ├── signalStrength (Property)
            ├── ssid (Property)
            ├── supportedAuthModes (Property)
            ├── scan (Action)
            ├── testSettings (Action)
            ├── configs (Entity Collection)
                ├── authConfig (Property)
                ├── id (Property)
                ├── ssid (Property)
```

### Entities

#### network-settings.v2 {#network-settings.v2}

- **Description**: System wide network configurations.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2}--->

##### Properties

###### modem_supported {#network-settings.v2.modem_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem_supported}--->

###### sfp_supported {#network-settings.v2.sfp_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin, admin, admin, admin, admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp_supported}--->

###### switch_supported {#network-settings.v2.switch_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch_supported}--->

###### wired_supported {#network-settings.v2.wired_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin, admin, admin, admin, admin, admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired_supported}--->

###### wlan_supported {#network-settings.v2.wlan_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan_supported}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.ip {#network-settings.v2.ip}

- **Description**: The IP parts.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.ip}--->

##### Properties

###### ipStatus {#network-settings.v2.ip.ipStatus}

- **Description**: The IP status of the current uplink (if any).
- **Datatype**: [IpStatus](#datatypes.IpStatus)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.ip.ipStatus}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.ip.configs {#network-settings.v2.ip.configs}

- **Description**: IP configurations.
- **Type**: Collection (Key Property: [id](#network-settings.v2.ip.configs.id))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.ip.configs}--->

##### Properties

###### id {#network-settings.v2.ip.configs.id}

- **Description**: The IP configuration identifier.
- **Datatype**: [IpConfigId](#datatypes.IpConfigId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.ip.configs.id}--->

###### ipv4 {#network-settings.v2.ip.configs.ipv4}

- **Description**: The IPv4 configuration.
- **Datatype**: [Ipv4Config](#datatypes.Ipv4Config)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.ip.configs.ipv4}--->

###### ipv6 {#network-settings.v2.ip.configs.ipv6}

- **Description**: The IPv6 configuration
- **Datatype**: [Ipv6Config](#datatypes.Ipv6Config)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.ip.configs.ipv6}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.modem {#network-settings.v2.modem}

- **Description**: The modem configuration and SIM management.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.modem}--->

##### Properties

###### imei {#network-settings.v2.modem.imei}

- **Description**: The IMEI of the modem, null if the modem is not ready.
- **Datatype**: [Imei](#datatypes.Imei)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.imei}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.modem.sim {#network-settings.v2.modem.sim}

- **Description**: The collection of SIM slots.
- **Type**: Collection (Key Property: [slotId](#network-settings.v2.modem.sim.slotId))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.modem.sim}--->

##### Properties

###### iccid {#network-settings.v2.modem.sim.iccid}

- **Description**: The Integrated Circuit Card Identifier of the SIM, null if the SIM is absent.
- **Datatype**: [Iccid](#datatypes.Iccid)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.iccid}--->

###### pinRetries {#network-settings.v2.modem.sim.pinRetries}

- **Description**: The number of attempts remaining for entering the SIM PIN, null if the SIM is absent.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.pinRetries}--->

###### provider {#network-settings.v2.modem.sim.provider}

- **Description**: The name of the SIM provider, null if the SIM is absent.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.provider}--->

###### pukRetries {#network-settings.v2.modem.sim.pukRetries}

- **Description**: The number of attempts remaining for entering the SIM PUK, null if the SIM is absent.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.pukRetries}--->

###### slotId {#network-settings.v2.modem.sim.slotId}

- **Description**: The slot ID of the SIM.
- **Datatype**: [SimSlotId](#datatypes.SimSlotId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.slotId}--->

###### status {#network-settings.v2.modem.sim.status}

- **Description**: The status of the SIM.
- **Datatype**: [SimStatus](#datatypes.SimStatus)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.status}--->

##### Actions

###### changeSimPin  {#network-settings.v2.modem.sim.changeSimPin}
- **Description**: Change the SIM PIN.
- **Request Datatype**: [SimPinOldAndNew](#datatypes.SimPinOldAndNew)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.changeSimPin}--->

###### setSimProtection  {#network-settings.v2.modem.sim.setSimProtection}
- **Description**: Set SIM PIN protection on or off.
- **Request Datatype**: [SimPinAndEnable](#datatypes.SimPinAndEnable)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.setSimProtection}--->

###### unblockSim  {#network-settings.v2.modem.sim.unblockSim}
- **Description**: Unblock the SIM card.
- **Request Datatype**: [SimPinAndPuk](#datatypes.SimPinAndPuk)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.unblockSim}--->

###### verifySimPin  {#network-settings.v2.modem.sim.verifySimPin}
- **Description**: Unlock the SIM using the PIN or just verify the PIN.
- **Request Datatype**: [SimPin](#datatypes.SimPin)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.verifySimPin}--->

---

#### network-settings.v2.modem.sim.connection {#network-settings.v2.modem.sim.connection}

- **Description**: The cellular network connection.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection}--->

##### Properties

###### connectionStatus {#network-settings.v2.modem.sim.connection.connectionStatus}

- **Description**: The connection status.
- **Datatype**: [CellularConnectionStatus](#datatypes.CellularConnectionStatus)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.connectionStatus}--->

###### operatorName {#network-settings.v2.modem.sim.connection.operatorName}

- **Description**: The operator name, null if not connected to a cellular network.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.operatorName}--->

###### radioInterface {#network-settings.v2.modem.sim.connection.radioInterface}

- **Description**: The connected radio interface.
- **Datatype**: [RadioInterface](#datatypes.RadioInterface)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.radioInterface}--->

##### Actions

###### getRadioParameters  {#network-settings.v2.modem.sim.connection.getRadioParameters}
- **Description**: Get the current radio parameters.
- **Request Datatype**: [RadioGeneration](#datatypes.RadioGeneration)
- **Response Datatype**: [RadioParameters](#datatypes.RadioParameters)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.getRadioParameters}--->

---

#### network-settings.v2.modem.sim.connection.networkInterface {#network-settings.v2.modem.sim.connection.networkInterface}

- **Description**: The collection of network interfaces for this SIM.
- **Type**: Collection (Key Property: [interfaceName](#network-settings.v2.modem.sim.connection.networkInterface.interfaceName))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.networkInterface}--->

##### Properties

###### accessPointName {#network-settings.v2.modem.sim.connection.networkInterface.accessPointName}

- **Description**: The Access Point Name for this interface.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.networkInterface.accessPointName}--->

###### interfaceName {#network-settings.v2.modem.sim.connection.networkInterface.interfaceName}

- **Description**: The interface name.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.networkInterface.interfaceName}--->

###### ipv4Info {#network-settings.v2.modem.sim.connection.networkInterface.ipv4Info}

- **Description**: The IPv4 information provided by the cellular network, null if an IPv4 connection is not established.
- **Datatype**: [Ipv4AddressStruct](#datatypes.Ipv4AddressStruct)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.networkInterface.ipv4Info}--->

##### Actions

###### setAccessPointName  {#network-settings.v2.modem.sim.connection.networkInterface.setAccessPointName}
- **Description**: Set the Access Point Name for a given interface.
- **Request Datatype**: [AccessPointName](#datatypes.AccessPointName)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.modem.sim.connection.networkInterface.setAccessPointName}--->

---

#### network-settings.v2.sfp {#network-settings.v2.sfp}

- **Description**: The SFP uplink configuration.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.sfp}--->

##### Properties

###### ipConfigId {#network-settings.v2.sfp.ipConfigId}

- **Description**: The IP configuration identifier.
- **Datatype**: [IpConfigId](#datatypes.IpConfigId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.ipConfigId}--->

###### linkMode {#network-settings.v2.sfp.linkMode}

- **Description**: The current link mode.
- **Datatype**: [LinkMode](#datatypes.LinkMode)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.linkMode}--->

###### lowerState {#network-settings.v2.sfp.lowerState}

- **Description**: Indicates if the SFP network interface device status is UP or DOWN.
- **Datatype**: [State](#datatypes.State)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.lowerState}--->

###### macAddress {#network-settings.v2.sfp.macAddress}

- **Description**: The MAC address of the SFP device.
- **Datatype**: [MacAddress](#datatypes.MacAddress)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.macAddress}--->

###### maxMtu {#network-settings.v2.sfp.maxMtu}

- **Description**: The maximum supported MTU of the SFP device.
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.maxMtu}--->

###### maxNumberOfVlans {#network-settings.v2.sfp.maxNumberOfVlans}

- **Description**: The maximum number of supported VLANs.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.maxNumberOfVlans}--->

###### minMtu {#network-settings.v2.sfp.minMtu}

- **Description**: The minimum supported MTU of the SFP device.
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.minMtu}--->

###### mtu {#network-settings.v2.sfp.mtu}

- **Description**: The MTU of the SFP device.
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.mtu}--->

###### staticLinkMode {#network-settings.v2.sfp.staticLinkMode}

- **Description**: The statically configured link mode.
- **Datatype**: [LinkMode](#datatypes.LinkMode)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.staticLinkMode}--->

###### staticMtu {#network-settings.v2.sfp.staticMtu}

- **Description**: The statically configured MTU of the SFP device.
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.staticMtu}--->

###### supportedLinkModes {#network-settings.v2.sfp.supportedLinkModes}

- **Description**: The supported link modes.
- **Datatype**: [LinkModes](#datatypes.LinkModes)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.supportedLinkModes}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.sfp.auth {#network-settings.v2.sfp.auth}

- **Description**: The authentication of the SFP device.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth}--->

##### Properties

###### macsecStatus {#network-settings.v2.sfp.auth.macsecStatus}

- **Description**: The MACsec authentication status of the SFP device.
- **Datatype**: [MacSecState](#datatypes.MacSecState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.macsecStatus}--->

###### mode {#network-settings.v2.sfp.auth.mode}

- **Description**: The current authentication mode. It must be set to one of the types of the configs.
- **Datatype**: [AuthMode](#datatypes.AuthMode)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.mode}--->

###### status {#network-settings.v2.sfp.auth.status}

- **Description**: The authentication status of the SFP device.
- **Datatype**: [AuthState](#datatypes.AuthState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.status}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.sfp.auth.configs {#network-settings.v2.sfp.auth.configs}

- **Description**: The authentication configurations for the SFP device.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.configs}--->

##### Properties

###### eapTls {#network-settings.v2.sfp.auth.configs.eapTls}

- **Description**: Specifies the EAP-TLS authentication configuration of the SFP device.
- **Datatype**: [AuthConfigEapTls](#datatypes.AuthConfigEapTls)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.configs.eapTls}--->

###### macsecPsk {#network-settings.v2.sfp.auth.configs.macsecPsk}

- **Description**: Specifies the MACsec PSK authentication configuration of the SFP device.
- **Datatype**: [AuthConfigMacsecPsk](#datatypes.AuthConfigMacsecPsk)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.configs.macsecPsk}--->

###### mschapv2 {#network-settings.v2.sfp.auth.configs.mschapv2}

- **Description**: Specifies the MSCHAPv2 authentication configuration of the SFP device.
- **Datatype**: [AuthConfigEapMschapV2](#datatypes.AuthConfigEapMschapV2)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.auth.configs.mschapv2}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.sfp.vlans {#network-settings.v2.sfp.vlans}

- **Description**: VLAN configurations.
- **Type**: Collection (Key Property: [id](#network-settings.v2.sfp.vlans.id))
- **Operations**
    - **Get**
    - **Add** (**Permissions**: admin)
        - **Required properties**: id
        - **Optional properties**: 
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.sfp.vlans}--->

##### Properties

###### id {#network-settings.v2.sfp.vlans.id}

- **Description**: The ID of the VLAN. Needs to be between 1 and 4094.
- **Datatype**: [VlanId](#datatypes.VlanId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.vlans.id}--->

###### ipConfigId {#network-settings.v2.sfp.vlans.ipConfigId}

- **Description**: The IP configuration identifier.
- **Datatype**: [IpConfigId](#datatypes.IpConfigId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.vlans.ipConfigId}--->

###### ipStatus {#network-settings.v2.sfp.vlans.ipStatus}

- **Description**: The IP status.
- **Datatype**: [IpStatus](#datatypes.IpStatus)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.vlans.ipStatus}--->

###### state {#network-settings.v2.sfp.vlans.state}

- **Description**: The interface state of the VLAN interface.
- **Datatype**: [InterfaceState](#datatypes.InterfaceState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.sfp.vlans.state}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.switch {#network-settings.v2.switch}

- **Description**: Global switch configurations.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.switch}--->

##### Properties

This entity has no properties.

##### Actions

This entity has no actions.

---

#### network-settings.v2.switch.port {#network-settings.v2.switch.port}

- **Description**: Switch port configurations.
- **Type**: Collection (Key Property: [portId](#network-settings.v2.switch.port.portId))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: security, enabled
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.switch.port}--->

##### Properties

###### enabled {#network-settings.v2.switch.port.enabled}

- **Description**: Specifies if a network interface device is enabled.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.enabled}--->

###### lowerState {#network-settings.v2.switch.port.lowerState}

- **Description**: Indicates if the network interface device status is UP or DOWN.
- **Datatype**: [State](#datatypes.State)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.lowerState}--->

###### portId {#network-settings.v2.switch.port.portId}

- **Description**: Switch port ID.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.portId}--->

###### remoteAddresses {#network-settings.v2.switch.port.remoteAddresses}

- **Description**: List containing all stored remote MAC addresses observed on the switch port.
- **Datatype**: [RemoteAddresses](#datatypes.RemoteAddresses)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.remoteAddresses}--->

###### security_supported {#network-settings.v2.switch.port.security_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.security_supported}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.switch.port.security {#network-settings.v2.switch.port.security}

- **Description**: Switch port security configurations.
- **Type**: Singleton
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: authServerEnabled, authServerEnforced
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.switch.port.security}--->

##### Properties

###### authServerEnabled {#network-settings.v2.switch.port.security.authServerEnabled}

- **Description**: Indicates if the authentication server is enabled.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.security.authServerEnabled}--->

###### authServerEnforced {#network-settings.v2.switch.port.security.authServerEnforced}

- **Description**: Indicates if the authentication server is enforced.
- **Datatype**: [AuthServerEnforced](#datatypes.AuthServerEnforced)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.security.authServerEnforced}--->

###### authState {#network-settings.v2.switch.port.security.authState}

- **Description**: Indicates the authentication state of the port.
- **Datatype**: [AuthState](#datatypes.AuthState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.security.authState}--->

###### macSecState {#network-settings.v2.switch.port.security.macSecState}

- **Description**: Indicates the MACSec state of the port.
- **Datatype**: [MacSecState](#datatypes.MacSecState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.switch.port.security.macSecState}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.system {#network-settings.v2.system}

- **Description**: The global network configuration.
- **Type**: Singleton
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: staticHostname, staticNameServers, staticSearchDomains, tcpEcn, uplinkBandwidthLimitation, useDhcpHostname, useDhcpResolver
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.system}--->

##### Properties

###### activeUplink {#network-settings.v2.system.activeUplink}

- **Description**: Indicates which is the uplink currently in use.
- **Datatype**: [Uplink](#datatypes.Uplink)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.activeUplink}--->

###### hostname {#network-settings.v2.system.hostname}

- **Description**: The currently used hostname.
- **Datatype**: [Hostname](#datatypes.Hostname)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.hostname}--->

###### nameServers {#network-settings.v2.system.nameServers}

- **Description**: The name servers currently in use.
- **Datatype**: [NameServers](#datatypes.NameServers)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.nameServers}--->

###### searchDomains {#network-settings.v2.system.searchDomains}

- **Description**: The search domains currently in use.
- **Datatype**: [SearchDomains](#datatypes.SearchDomains)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.searchDomains}--->

###### staticHostname {#network-settings.v2.system.staticHostname}

- **Description**: The statically configured hostname. It is used if useDhcpHostname is set to false, or if no hostname was obtained by DHCP.
- **Datatype**: [Hostname](#datatypes.Hostname)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.staticHostname}--->

###### staticNameServers {#network-settings.v2.system.staticNameServers}

- **Description**: The statically configured name servers. It is used if useDhcpResolver is set to false, or if no name servers were obtained by DHCP.
- **Datatype**: [NameServers](#datatypes.NameServers)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.staticNameServers}--->

###### staticSearchDomains {#network-settings.v2.system.staticSearchDomains}

- **Description**: The statically configured search domains. It is used if useDhcpResolver is set to false, or if no search domains were obtained by DHCP.
- **Datatype**: [SearchDomains](#datatypes.SearchDomains)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.staticSearchDomains}--->

###### tcpEcn {#network-settings.v2.system.tcpEcn}

- **Description**: The TCP Explicit Congestion Notification (TCP ECN) setting.
- **Datatype**: [TcpEcn](#datatypes.TcpEcn)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.tcpEcn}--->

###### uplinkBandwidthLimitation {#network-settings.v2.system.uplinkBandwidthLimitation}

- **Description**: The bandwidth limitation for the current uplink
- **Datatype**: [BandwidthLimit](#datatypes.BandwidthLimit)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.uplinkBandwidthLimitation}--->

###### useDhcpHostname {#network-settings.v2.system.useDhcpHostname}

- **Description**: Checks which DHCP-assigned hostname should be used. If set to false, the staticHostname will be used.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.useDhcpHostname}--->

###### useDhcpResolver {#network-settings.v2.system.useDhcpResolver}

- **Description**: Checks if any DHCP-assigned resolver information should be used. If set to false, the static resolver values will be used.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.system.useDhcpResolver}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wired {#network-settings.v2.wired}

- **Description**: The wired uplink configuration.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.wired}--->

##### Properties

###### ipConfigId {#network-settings.v2.wired.ipConfigId}

- **Description**: The IP configuration identifier.
- **Datatype**: [IpConfigId](#datatypes.IpConfigId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.ipConfigId}--->

###### linkMode {#network-settings.v2.wired.linkMode}

- **Description**: The current link mode.
- **Datatype**: [LinkMode](#datatypes.LinkMode)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.linkMode}--->

###### lowerState {#network-settings.v2.wired.lowerState}

- **Description**: Indicates if the wired network interface device status is UP or DOWN.
- **Datatype**: [State](#datatypes.State)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.lowerState}--->

###### macAddress {#network-settings.v2.wired.macAddress}

- **Description**: The MAC address of the wired device.
- **Datatype**: [MacAddress](#datatypes.MacAddress)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.macAddress}--->

###### managesSfp {#network-settings.v2.wired.managesSfp}

- **Description**: Indicator if this wired device also manages any SFP device. If SFP is managed by the wired device, then the SFP entity is not set, even if the product has support for SFP.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.managesSfp}--->

###### maxMtu {#network-settings.v2.wired.maxMtu}

- **Description**: The maximum supported MTU of the wired device
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.maxMtu}--->

###### maxNumberOfVlans {#network-settings.v2.wired.maxNumberOfVlans}

- **Description**: The maximum number of supported VLANs.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.maxNumberOfVlans}--->

###### minMtu {#network-settings.v2.wired.minMtu}

- **Description**: The minimum supported MTU of the wired device
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.minMtu}--->

###### mtu {#network-settings.v2.wired.mtu}

- **Description**: The MTU of the wired device
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.mtu}--->

###### staticLinkMode {#network-settings.v2.wired.staticLinkMode}

- **Description**: The statically configured link mode.
- **Datatype**: [LinkMode](#datatypes.LinkMode)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.staticLinkMode}--->

###### staticMtu {#network-settings.v2.wired.staticMtu}

- **Description**: The statically configured MTU of the wired device
- **Datatype**: [Mtu](#datatypes.Mtu)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.staticMtu}--->

###### supportedLinkModes {#network-settings.v2.wired.supportedLinkModes}

- **Description**: The supported link modes.
- **Datatype**: [LinkModes](#datatypes.LinkModes)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.supportedLinkModes}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wired.auth {#network-settings.v2.wired.auth}

- **Description**: The authentication of the wired device.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.wired.auth}--->

##### Properties

###### macsecStatus {#network-settings.v2.wired.auth.macsecStatus}

- **Description**: The MACsec authentication status of the wired device.
- **Datatype**: [MacSecState](#datatypes.MacSecState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.macsecStatus}--->

###### mode {#network-settings.v2.wired.auth.mode}

- **Description**: The current authentication mode. It must be set to one of the types of the configs.
- **Datatype**: [AuthMode](#datatypes.AuthMode)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.mode}--->

###### status {#network-settings.v2.wired.auth.status}

- **Description**: The authentication status of the wired device.
- **Datatype**: [AuthState](#datatypes.AuthState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.status}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wired.auth.configs {#network-settings.v2.wired.auth.configs}

- **Description**: The authentication configurations for the wired device.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.configs}--->

##### Properties

###### eapTls {#network-settings.v2.wired.auth.configs.eapTls}

- **Description**: Specifies the EAP-TLS authentication configuration of the wired device.
- **Datatype**: [AuthConfigEapTls](#datatypes.AuthConfigEapTls)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.configs.eapTls}--->

###### macsecPsk {#network-settings.v2.wired.auth.configs.macsecPsk}

- **Description**: Specifies the MACsec PSK authentication configuration of the wired device.
- **Datatype**: [AuthConfigMacsecPsk](#datatypes.AuthConfigMacsecPsk)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.configs.macsecPsk}--->

###### mschapv2 {#network-settings.v2.wired.auth.configs.mschapv2}

- **Description**: Specifies the MSCHAPv2 authentication configuration of the wired device.
- **Datatype**: [AuthConfigEapMschapV2](#datatypes.AuthConfigEapMschapV2)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.auth.configs.mschapv2}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wired.vlans {#network-settings.v2.wired.vlans}

- **Description**: VLAN configurations.
- **Type**: Collection (Key Property: [id](#network-settings.v2.wired.vlans.id))
- **Operations**
    - **Get**
    - **Add** (**Permissions**: admin)
        - **Required properties**: id
        - **Optional properties**: 
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.wired.vlans}--->

##### Properties

###### id {#network-settings.v2.wired.vlans.id}

- **Description**: The ID of the VLAN. Needs to be between 1 and 4094.
- **Datatype**: [VlanId](#datatypes.VlanId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.vlans.id}--->

###### ipConfigId {#network-settings.v2.wired.vlans.ipConfigId}

- **Description**: The IP configuration identifier.
- **Datatype**: [IpConfigId](#datatypes.IpConfigId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.vlans.ipConfigId}--->

###### ipStatus {#network-settings.v2.wired.vlans.ipStatus}

- **Description**: The IP status.
- **Datatype**: [IpStatus](#datatypes.IpStatus)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.vlans.ipStatus}--->

###### state {#network-settings.v2.wired.vlans.state}

- **Description**: The interface state of the VLAN interface.
- **Datatype**: [InterfaceState](#datatypes.InterfaceState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wired.vlans.state}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wlan {#network-settings.v2.wlan}

- **Description**: The WLAN configuration.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.wlan}--->

##### Properties

###### ap_supported {#network-settings.v2.wlan.ap_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.ap_supported}--->

###### countryCode {#network-settings.v2.wlan.countryCode}

- **Description**: The country code following the ISO 3166-1 alpha-2 standard.
- **Datatype**: [CountryCode](#datatypes.CountryCode)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.countryCode}--->

###### countryCodeLocked {#network-settings.v2.wlan.countryCodeLocked}

- **Description**: The locked state for setting the country code. If locked, it is not possible to configure it.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.countryCodeLocked}--->

###### station_supported {#network-settings.v2.wlan.station_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin, admin, admin, admin, admin, admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station_supported}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wlan.ap {#network-settings.v2.wlan.ap}

- **Description**: The WLAN Access Point status.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.wlan.ap}--->

##### Properties

###### authMode {#network-settings.v2.wlan.ap.authMode}

- **Description**: The configured authentication configuration mode.
- **Datatype**: [AuthMode](#datatypes.AuthMode)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.ap.authMode}--->

###### enabled {#network-settings.v2.wlan.ap.enabled}

- **Description**: The current enabled state of the WLAN AP.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.ap.enabled}--->

###### setupServer_supported {#network-settings.v2.wlan.ap.setupServer_supported}

- **Description**: 
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.ap.setupServer_supported}--->

###### ssid {#network-settings.v2.wlan.ap.ssid}

- **Description**: The configured SSID.
- **Datatype**: [Ssid](#datatypes.Ssid)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.ap.ssid}--->

##### Actions

This entity has no actions.

---

#### network-settings.v2.wlan.ap.setupServer {#network-settings.v2.wlan.ap.setupServer}

- **Description**: The WLAN setup server configurations.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.wlan.ap.setupServer}--->

##### Properties

This entity has no properties.

##### Actions

###### switchApToStation  {#network-settings.v2.wlan.ap.setupServer.switchApToStation}
- **Description**: Trigger a switch between AP and station mode. This is only used for installation mode. The WLAN station needs to be properly configured for this to be allowed.
- **Request Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Response Datatype**: [EmptyJson](#datatypes.EmptyJson)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.wlan.ap.setupServer.switchApToStation}--->

---

#### network-settings.v2.wlan.station {#network-settings.v2.wlan.station}

- **Description**: The WLAN station configurations.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: Yes

<!---{Extra-Doc:network-settings.v2.wlan.station}--->

##### Properties

###### authState {#network-settings.v2.wlan.station.authState}

- **Description**: The authentication state of the WLAN station.
- **Datatype**: [AuthState](#datatypes.AuthState)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.authState}--->

###### enabled {#network-settings.v2.wlan.station.enabled}

- **Description**: The current enabled state of the WLAN station. It is only allowed to enable the station if all APs are disabled.
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.enabled}--->

###### maxNumberOfAuthConfigs {#network-settings.v2.wlan.station.maxNumberOfAuthConfigs}

- **Description**: The maximum number of supported authentication configurations.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.maxNumberOfAuthConfigs}--->

###### signalStrength {#network-settings.v2.wlan.station.signalStrength}

- **Description**: The current signal strength (in dBm). Set to null if no SSID is connected.
- **Datatype**: integer
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.signalStrength}--->

###### ssid {#network-settings.v2.wlan.station.ssid}

- **Description**: The currently used SSID. Set to null if no SSID is connected.
- **Datatype**: [Ssid](#datatypes.Ssid)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.ssid}--->

###### supportedAuthModes {#network-settings.v2.wlan.station.supportedAuthModes}

- **Description**: The supported authentication configuration modes.
- **Datatype**: [AuthModes](#datatypes.AuthModes)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.supportedAuthModes}--->

##### Actions

###### scan  {#network-settings.v2.wlan.station.scan}
- **Description**: Scan the network for access points.
- **Request Datatype**: [ScanRequestData](#datatypes.ScanRequestData)
- **Response Datatype**: [ScanResponseData](#datatypes.ScanResponseData)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.wlan.station.scan}--->

###### testSettings  {#network-settings.v2.wlan.station.testSettings}
- **Description**: Test the current settings to check for connectivity.
- **Request Datatype**: [TestSettingsRequestData](#datatypes.TestSettingsRequestData)
- **Response Datatype**: [TestSettingsResponseData](#datatypes.TestSettingsResponseData)
- **Trigger Permissions**: admin

<!---{Extra-Doc:network-settings.v2.wlan.station.testSettings}--->

---

#### network-settings.v2.wlan.station.configs {#network-settings.v2.wlan.station.configs}

- **Description**: The configurations of the WLAN station. Currently, only one authentication configuration is supported, but in the future it might support multiple configurations by using add and remove.
- **Type**: Collection (Key Property: [id](#network-settings.v2.wlan.station.configs.id))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: authConfig, ssid
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.configs}--->

##### Properties

###### authConfig {#network-settings.v2.wlan.station.configs.authConfig}

- **Description**: The authentication configuration.
- **Datatype**: [AuthConfig](#datatypes.AuthConfig)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.configs.authConfig}--->

###### id {#network-settings.v2.wlan.station.configs.id}

- **Description**: The identifier of the station configuration.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.configs.id}--->

###### ssid {#network-settings.v2.wlan.station.configs.ssid}

- **Description**: The SSID of the AP to authenticate against.
- **Datatype**: [Ssid](#datatypes.Ssid)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:network-settings.v2.wlan.station.configs.ssid}--->

##### Actions

This entity has no actions.

---

### Data Types

#### AccessPointName {#datatypes.AccessPointName}

- **Description**: The Access Point Name (APN).
- **Type**: string
- **Minimum Length**: 0
- **Maximum Length**: 100
- **Pattern**: ^[a-zA-Z0-9.-]*$

<!---{Extra-Doc:datatypes.AccessPointName}--->

#### AuthConfig {#datatypes.AuthConfig}


<!---{Extra-Doc:datatypes.AuthConfig}--->

#### AuthConfigEapMschapV2 {#datatypes.AuthConfigEapMschapV2}

- **Description**: The WPA-Enterprise MSCHAPv2 authentication configuration type.
- **Type**: complex
- **Fields**
    - **caCerts**
        - **Description**: The list of trusted CA certificate ids.
        - **Type**: [CertificateIds](#datatypes.CertificateIds)
        - **Nullable**: No / **Gettable**: No
    - **eapolVersion**
        - **Description**: The EAPoL version.
        - **Type**: [EapolVersion](#datatypes.EapolVersion)
        - **Nullable**: No / **Gettable**: No
    - **identity**
        - **Description**: The user identity presented to the network. Cannot be null on set.
        - **Type**: [MschapV2Id](#datatypes.MschapV2Id)
        - **Nullable**: Yes / **Gettable**: No
    - **password**
        - **Description**: The user password presented to the network.
        - **Type**: [MschapV2Password](#datatypes.MschapV2Password)
        - **Nullable**: No / **Gettable**: No
    - **peap**
        - **Description**: The PEAP parts.
        - **Type**: [Peap](#datatypes.Peap)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.AuthConfigEapMschapV2}--->

#### AuthConfigEapTls {#datatypes.AuthConfigEapTls}

- **Description**: The WPA-Enterprise EAP-TLS authentication configuration type.
- **Type**: complex
- **Fields**
    - **caCerts**
        - **Description**: The list of trusted CA certificate ids.
        - **Type**: [CertificateIds](#datatypes.CertificateIds)
        - **Nullable**: No / **Gettable**: No
    - **clientCertId**
        - **Description**: The client certificate ID. Cannot be null on set.
        - **Type**: [CertificateId](#datatypes.CertificateId)
        - **Nullable**: Yes / **Gettable**: No
    - **eapolVersion**
        - **Description**: The EAPoL version.
        - **Type**: [EapolVersion](#datatypes.EapolVersion)
        - **Nullable**: No / **Gettable**: No
    - **identity**
        - **Description**: The identity associated with the certificate. Cannot be null on set.
        - **Type**: [EapTlsIdentity](#datatypes.EapTlsIdentity)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.AuthConfigEapTls}--->

#### AuthConfigMacsecPsk {#datatypes.AuthConfigMacsecPsk}

- **Description**: The MACsec pre-shared-key (static CAK) authentication configuration type.
- **Type**: complex
- **Fields**
    - **cak**
        - **Description**: The MACsec connectivity association key.
        - **Type**: [MacsecCak](#datatypes.MacsecCak)
        - **Nullable**: No / **Gettable**: No
    - **ckn**
        - **Description**: The MACsec connectivity association key name. Cannot be null on set.
        - **Type**: [MacsecCkn](#datatypes.MacsecCkn)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.AuthConfigMacsecPsk}--->

#### AuthConfigNone {#datatypes.AuthConfigNone}

- **Description**: No authentication configuration type.
- **Type**: complex
- **Fields**

<!---{Extra-Doc:datatypes.AuthConfigNone}--->

#### AuthConfigWpaPassphrase {#datatypes.AuthConfigWpaPassphrase}

- **Description**: WPA passphrase authentication configuration type.
- **Type**: complex
- **Fields**
    - **psk**
        - **Description**: WPA authentication passphrase.
        - **Type**: [AuthWpaPassphrase](#datatypes.AuthWpaPassphrase)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.AuthConfigWpaPassphrase}--->

#### AuthConfigWpaPsk {#datatypes.AuthConfigWpaPsk}

- **Description**: WPA pre-shared key authentication configuration type.
- **Type**: complex
- **Fields**
    - **psk**
        - **Description**: Authentication WPA pre-shared key.
        - **Type**: [AuthWpaPsk](#datatypes.AuthWpaPsk)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.AuthConfigWpaPsk}--->

#### AuthMode {#datatypes.AuthMode}

- **Description**: The authentication mode type.
- **Type**: string
- **Enum Values**: "NONE", "WPA_PASSPHRASE", "WPA_PSK", "EAP_TLS", "EAP_MSCHAPV2", "MACSEC_PSK"

<!---{Extra-Doc:datatypes.AuthMode}--->

#### AuthModes {#datatypes.AuthModes}

- **Description**: An array of authentication mode types.
- **Type**: array
- **Element type**: [AuthMode](#datatypes.AuthMode)
- **Null Value**: No

<!---{Extra-Doc:datatypes.AuthModes}--->

#### AuthServerEnforced {#datatypes.AuthServerEnforced}

- **Description**: The authentication server enforcement level.
- **Type**: string
- **Enum Values**: "NONE", "AUTHENTICATED", "MACSEC_SECURED"

<!---{Extra-Doc:datatypes.AuthServerEnforced}--->

#### AuthState {#datatypes.AuthState}

- **Description**: The authentication state type.
- **Type**: string
- **Enum Values**: "UNKNOWN", "AUTHENTICATED", "AUTHENTICATING", "STOPPED", "FAILED"

<!---{Extra-Doc:datatypes.AuthState}--->

#### AuthWpaPassphrase {#datatypes.AuthWpaPassphrase}

- **Description**: The passphrase type.
- **Type**: string
- **Minimum Length**: 8
- **Maximum Length**: 63

<!---{Extra-Doc:datatypes.AuthWpaPassphrase}--->

#### AuthWpaPsk {#datatypes.AuthWpaPsk}

- **Description**: The pre-shared-key type.
- **Type**: string
- **Minimum Length**: 64
- **Maximum Length**: 64
- **Pattern**: ^[a-fA-F0-9]{64}$

<!---{Extra-Doc:datatypes.AuthWpaPsk}--->

#### BandwidthLimit {#datatypes.BandwidthLimit}

- **Description**: The limit value type in KBps. Set to 0 to disable bandwidth limitation.
- **Type**: integer
- **Minimum Value**: 0
- **Maximum Value**: 10000000

<!---{Extra-Doc:datatypes.BandwidthLimit}--->

#### CellularConnectionStatus {#datatypes.CellularConnectionStatus}

- **Description**: The initial state is no network registration, followed by registered without data connection, then data connection without IP and finally IP assigned.

- **Type**: string
- **Enum Values**: "NOT_REGISTERED", "REGISTERED", "DATA_ENABLED", "IP_ASSIGNED"

<!---{Extra-Doc:datatypes.CellularConnectionStatus}--->

#### CertificateId {#datatypes.CertificateId}

- **Description**: The certificate ID type.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 128

<!---{Extra-Doc:datatypes.CertificateId}--->

#### CertificateIds {#datatypes.CertificateIds}

- **Description**: A list of certificate IDs.
- **Type**: array
- **Element type**: [CertificateId](#datatypes.CertificateId)
- **Null Value**: No
- **Minimum item number**: 0
- **Maximum item number**: 5

<!---{Extra-Doc:datatypes.CertificateIds}--->

#### CountryCode {#datatypes.CountryCode}

- **Description**: WLAN country code type.
- **Type**: string
- **Pattern**: ^[A-Z]{2}$

<!---{Extra-Doc:datatypes.CountryCode}--->

#### DomainName {#datatypes.DomainName}

- **Description**: Domain name type.
- **Type**: string

<!---{Extra-Doc:datatypes.DomainName}--->

#### EapTlsIdentity {#datatypes.EapTlsIdentity}

- **Description**: The EAP-TLS identity associated with the certificate.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 32

<!---{Extra-Doc:datatypes.EapTlsIdentity}--->

#### EapolVersion {#datatypes.EapolVersion}

- **Description**: The EAPoL version type.
- **Type**: integer
- **Minimum Value**: 1
- **Maximum Value**: 3

<!---{Extra-Doc:datatypes.EapolVersion}--->

#### EmptyJson {#datatypes.EmptyJson}

- **Description**: Empty response object.
- **Type**: complex
- **Fields**

<!---{Extra-Doc:datatypes.EmptyJson}--->

#### Hostname {#datatypes.Hostname}

- **Description**: Hostname type.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 63
- **Pattern**: ^[a-zA-Z0-9]$|^[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]$

<!---{Extra-Doc:datatypes.Hostname}--->

#### Iccid {#datatypes.Iccid}

- **Description**: The Integrated Circuit Card Identification number.
- **Type**: string

<!---{Extra-Doc:datatypes.Iccid}--->

#### Imei {#datatypes.Imei}

- **Description**: The IMEI type.
- **Type**: string
- **Minimum Length**: 15
- **Maximum Length**: 15

<!---{Extra-Doc:datatypes.Imei}--->

#### InterfaceState {#datatypes.InterfaceState}

- **Description**: The interface state type.
- **Type**: string
- **Enum Values**: "PENDING", "READY"

<!---{Extra-Doc:datatypes.InterfaceState}--->

#### IpAddress {#datatypes.IpAddress}

- **Description**: IPv4 or IPv6 address type.
- **Type**: string

<!---{Extra-Doc:datatypes.IpAddress}--->

#### IpConfigId {#datatypes.IpConfigId}

- **Description**: The identifier for an IP configuration.
- **Type**: string

<!---{Extra-Doc:datatypes.IpConfigId}--->

#### IpStatus {#datatypes.IpStatus}

- **Description**: IP status.
- **Type**: complex
- **Fields**
    - **ipv4**
        - **Description**: The IPv4 status.
        - **Type**: [Ipv4Status](#datatypes.Ipv4Status)
        - **Nullable**: No / **Gettable**: No
    - **ipv6**
        - **Description**: The IPv6 status.
        - **Type**: [Ipv6Status](#datatypes.Ipv6Status)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.IpStatus}--->

#### Ipv4Address {#datatypes.Ipv4Address}

- **Description**: The IPv4 address type.
- **Type**: string
- **Pattern**: ^(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)\\.(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)\\.(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)\\.(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)$

<!---{Extra-Doc:datatypes.Ipv4Address}--->

#### Ipv4AddressOrigin {#datatypes.Ipv4AddressOrigin}

- **Description**: The origin type of the IPv4 address.
- **Type**: string
- **Enum Values**: "CELLULAR", "DHCP", "STATIC", "LINKLOCAL", "UNKNOWN"

<!---{Extra-Doc:datatypes.Ipv4AddressOrigin}--->

#### Ipv4AddressStruct {#datatypes.Ipv4AddressStruct}

- **Description**: The IPv4 address structure type.
- **Type**: complex
- **Fields**
    - **ipAddress**
        - **Description**: The IPv4 address of the address structure.
        - **Type**: [Ipv4Address](#datatypes.Ipv4Address)
        - **Nullable**: No / **Gettable**: No
    - **origin**
        - **Description**: The IPv4 address origin of the address structure.
        - **Type**: [Ipv4AddressOrigin](#datatypes.Ipv4AddressOrigin)
        - **Nullable**: No / **Gettable**: No
    - **prefixLength**
        - **Description**: The IPv4 address prefix length of the address structure.
        - **Type**: [Ipv4PrefixLength](#datatypes.Ipv4PrefixLength)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv4AddressStruct}--->

#### Ipv4Addresses {#datatypes.Ipv4Addresses}

- **Description**: IPv4 addresses type.
- **Type**: array
- **Element type**: [Ipv4AddressStruct](#datatypes.Ipv4AddressStruct)
- **Null Value**: No

<!---{Extra-Doc:datatypes.Ipv4Addresses}--->

#### Ipv4Config {#datatypes.Ipv4Config}

- **Description**: IPv4 config.
- **Type**: complex
- **Fields**
    - **configurationMode**
        - **Description**: The configuration mode.
        - **Type**: [Ipv4ConfigurationMode](#datatypes.Ipv4ConfigurationMode)
        - **Nullable**: No / **Gettable**: No
    - **enabled**
        - **Description**: The IPv4 enabled state.
        - **Type**: boolean
        - **Nullable**: No / **Gettable**: No
    - **linkLocalMode**
        - **Description**: The link-local configuration mode.
        - **Type**: [Ipv4LinkLocalConfigurationMode](#datatypes.Ipv4LinkLocalConfigurationMode)
        - **Nullable**: No / **Gettable**: No
    - **staticAddressConfig**
        - **Description**: The static IPv4 address configuration.
        - **Type**: [Ipv4StaticAddressConfig](#datatypes.Ipv4StaticAddressConfig)
        - **Nullable**: No / **Gettable**: No
    - **staticDefaultRouter**
        - **Description**: The statically assigned default router.
        - **Type**: [Ipv4Address](#datatypes.Ipv4Address)
        - **Nullable**: No / **Gettable**: No
    - **vendorClassIdentifier**
        - **Description**: The Vendor Class Identifier advertised by the DHCP client.
        - **Type**: [VendorClassIdentifier](#datatypes.VendorClassIdentifier)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv4Config}--->

#### Ipv4ConfigurationMode {#datatypes.Ipv4ConfigurationMode}

- **Description**: The IPv4 address configuration modes.
- **Type**: string
- **Enum Values**: "DHCP", "STATIC"

<!---{Extra-Doc:datatypes.Ipv4ConfigurationMode}--->

#### Ipv4LinkLocalConfigurationMode {#datatypes.Ipv4LinkLocalConfigurationMode}

- **Description**: The IPv4 link-local address configuration modes.
- **Type**: string
- **Enum Values**: "ON", "OFF", "FALLBACK"

<!---{Extra-Doc:datatypes.Ipv4LinkLocalConfigurationMode}--->

#### Ipv4PrefixLength {#datatypes.Ipv4PrefixLength}

- **Description**: The IPv4 prefix length type.
- **Type**: integer
- **Minimum Value**: 0
- **Maximum Value**: 32

<!---{Extra-Doc:datatypes.Ipv4PrefixLength}--->

#### Ipv4StaticAddressConfig {#datatypes.Ipv4StaticAddressConfig}

- **Description**: IPv4 static address configuration.
- **Type**: complex
- **Fields**
    - **address**
        - **Description**: The statically assigned IPv4 address.
        - **Type**: [Ipv4Address](#datatypes.Ipv4Address)
        - **Nullable**: No / **Gettable**: No
    - **broadcastAddress**
        - **Description**: The statically assigned IPv4 broadcast address. If set to null, it will be automatically calculated.
        - **Type**: [Ipv4Address](#datatypes.Ipv4Address)
        - **Nullable**: Yes / **Gettable**: No
    - **prefixLength**
        - **Description**: The statically assigned IPv4 prefix length.
        - **Type**: [Ipv4PrefixLength](#datatypes.Ipv4PrefixLength)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv4StaticAddressConfig}--->

#### Ipv4Status {#datatypes.Ipv4Status}

- **Description**: IPv4 status.
- **Type**: complex
- **Fields**
    - **addresses**
        - **Description**: A list of currently used IPv4 addresses.
        - **Type**: [Ipv4Addresses](#datatypes.Ipv4Addresses)
        - **Nullable**: No / **Gettable**: No
    - **defaultRouter**
        - **Description**: The default router currently in use.
        - **Type**: [Ipv4Address](#datatypes.Ipv4Address)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv4Status}--->

#### Ipv6Address {#datatypes.Ipv6Address}

- **Description**: The IPv6 address type.
- **Type**: string
- **Minimum Length**: 2
- **Maximum Length**: 39
- **Pattern**: ^[0-9a-fA-F:]+$

<!---{Extra-Doc:datatypes.Ipv6Address}--->

#### Ipv6AddressOrigin {#datatypes.Ipv6AddressOrigin}

- **Description**: The origin type of the IPv6 address.
- **Type**: string
- **Enum Values**: "CELLULAR", "DHCP", "STATIC", "LINKLOCAL", "RA", "UNKNOWN"

<!---{Extra-Doc:datatypes.Ipv6AddressOrigin}--->

#### Ipv6AddressStruct {#datatypes.Ipv6AddressStruct}

- **Description**: The IPv6 address structure type.
- **Type**: complex
- **Fields**
    - **ipAddress**
        - **Description**: The IPv6 address of the address structure.
        - **Type**: [Ipv6Address](#datatypes.Ipv6Address)
        - **Nullable**: No / **Gettable**: No
    - **origin**
        - **Description**: The IPv6 address origin of the address structure.
        - **Type**: [Ipv6AddressOrigin](#datatypes.Ipv6AddressOrigin)
        - **Nullable**: No / **Gettable**: No
    - **prefixLength**
        - **Description**: The IPv6 address prefix length of the address structure.
        - **Type**: [Ipv6PrefixLength](#datatypes.Ipv6PrefixLength)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv6AddressStruct}--->

#### Ipv6Addresses {#datatypes.Ipv6Addresses}

- **Description**: The IPv6 addresses type.
- **Type**: array
- **Element type**: [Ipv6AddressStruct](#datatypes.Ipv6AddressStruct)
- **Null Value**: No

<!---{Extra-Doc:datatypes.Ipv6Addresses}--->

#### Ipv6AutoConfigurationMode {#datatypes.Ipv6AutoConfigurationMode}

- **Description**: The IPv6 automatic address configuration modes.
- **Type**: string
- **Enum Values**: "AUTO", "STATELESS", "STATEFUL", "OFF"

<!---{Extra-Doc:datatypes.Ipv6AutoConfigurationMode}--->

#### Ipv6Config {#datatypes.Ipv6Config}

- **Description**: The IPv6 config parameter.
- **Type**: complex
- **Fields**
    - **autoConfigurationMode**
        - **Description**: The automatic address configuration mode.
        - **Type**: [Ipv6AutoConfigurationMode](#datatypes.Ipv6AutoConfigurationMode)
        - **Nullable**: No / **Gettable**: No
    - **enabled**
        - **Description**: The IPv6 enabled state.
        - **Type**: boolean
        - **Nullable**: No / **Gettable**: No
    - **staticAddresses**
        - **Description**: IPv6 static addresses.
        - **Type**: [Ipv6StaticAddresses](#datatypes.Ipv6StaticAddresses)
        - **Nullable**: No / **Gettable**: No
    - **staticDefaultRouter**
        - **Description**: The statically configured IPv6 default router.
        - **Type**: [Ipv6Address](#datatypes.Ipv6Address)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv6Config}--->

#### Ipv6DefaultRouters {#datatypes.Ipv6DefaultRouters}

- **Description**: The IPv6 default routers type.
- **Type**: array
- **Element type**: [Ipv6Address](#datatypes.Ipv6Address)
- **Null Value**: No

<!---{Extra-Doc:datatypes.Ipv6DefaultRouters}--->

#### Ipv6PrefixLength {#datatypes.Ipv6PrefixLength}

- **Description**: The IPv6 prefix length type.
- **Type**: integer
- **Minimum Value**: 0
- **Maximum Value**: 128

<!---{Extra-Doc:datatypes.Ipv6PrefixLength}--->

#### Ipv6StaticAddress {#datatypes.Ipv6StaticAddress}

- **Description**: The IPv6 static address configuration.
- **Type**: complex
- **Fields**
    - **address**
        - **Description**: The statically assigned IPv6 address.
        - **Type**: [Ipv6Address](#datatypes.Ipv6Address)
        - **Nullable**: No / **Gettable**: No
    - **prefixLength**
        - **Description**: The statically assigned IPv6 prefix length.
        - **Type**: [Ipv6PrefixLength](#datatypes.Ipv6PrefixLength)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv6StaticAddress}--->

#### Ipv6StaticAddresses {#datatypes.Ipv6StaticAddresses}

- **Description**: The IPv6 addresses type.
- **Type**: array
- **Element type**: [Ipv6StaticAddress](#datatypes.Ipv6StaticAddress)
- **Null Value**: No
- **Minimum item number**: 0
- **Maximum item number**: 5

<!---{Extra-Doc:datatypes.Ipv6StaticAddresses}--->

#### Ipv6Status {#datatypes.Ipv6Status}

- **Description**: The IPv6 status.
- **Type**: complex
- **Fields**
    - **addresses**
        - **Description**: A list of currently used IPv6 addresses.
        - **Type**: [Ipv6Addresses](#datatypes.Ipv6Addresses)
        - **Nullable**: No / **Gettable**: No
    - **defaultRouters**
        - **Description**: A list of currently used IPv6 default routers.
        - **Type**: [Ipv6DefaultRouters](#datatypes.Ipv6DefaultRouters)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Ipv6Status}--->

#### KeyMgmt {#datatypes.KeyMgmt}

- **Description**: The key management type.
- **Type**: complex
- **Fields**
    - **suite**
        - **Description**: The key management suite.
        - **Type**: [KeyMgmtSuite](#datatypes.KeyMgmtSuite)
        - **Nullable**: No / **Gettable**: No
    - **wpaVersion**
        - **Description**: The WPA version.
        - **Type**: [WpaVersion](#datatypes.WpaVersion)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.KeyMgmt}--->

#### KeyMgmtSuite {#datatypes.KeyMgmtSuite}

- **Description**: The key management suite type.
- **Type**: string
- **Enum Values**: "WPA_NONE", "WPA_PSK", "WPA_EAP"

<!---{Extra-Doc:datatypes.KeyMgmtSuite}--->

#### KeyMgmts {#datatypes.KeyMgmts}

- **Description**: The supported key management types.
- **Type**: array
- **Element type**: [KeyMgmt](#datatypes.KeyMgmt)
- **Null Value**: No

<!---{Extra-Doc:datatypes.KeyMgmts}--->

#### LinkMode {#datatypes.LinkMode}

- **Description**: The link speed and duplex mode of the wired connection.
- **Type**: string
- **Enum Values**: "UNKNOWN", "AUTO", "10HD", "10FD", "100HD", "100FD", "1000HD", "1000FD"

<!---{Extra-Doc:datatypes.LinkMode}--->

#### LinkModes {#datatypes.LinkModes}

- **Description**: A list of link modes.
- **Type**: array
- **Element type**: [LinkMode](#datatypes.LinkMode)
- **Null Value**: No

<!---{Extra-Doc:datatypes.LinkModes}--->

#### MacAddress {#datatypes.MacAddress}

- **Description**: The MAC address type.
- **Type**: string
- **Pattern**: ^([0-9A-Fa-f]{2}[:]){5}([0-9A-Fa-f]{2})$

<!---{Extra-Doc:datatypes.MacAddress}--->

#### MacSecState {#datatypes.MacSecState}

- **Description**: The MACsec state type.
- **Type**: string
- **Enum Values**: "UNKNOWN", "SECURED", "CONNECTING", "STOPPED", "FAILED"

<!---{Extra-Doc:datatypes.MacSecState}--->

#### MacsecCak {#datatypes.MacsecCak}

- **Description**: The MACsec connectivity association key.
- **Type**: string
- **Minimum Length**: 32
- **Maximum Length**: 64
- **Pattern**: ^[a-fA-F0-9]{32}([a-fA-F0-9]{32})?$

<!---{Extra-Doc:datatypes.MacsecCak}--->

#### MacsecCkn {#datatypes.MacsecCkn}

- **Description**: The MACsec connectivity association key name.
- **Type**: string
- **Minimum Length**: 2
- **Maximum Length**: 64
- **Pattern**: ^(?:[A-Fa-f0-9]{2})+$

<!---{Extra-Doc:datatypes.MacsecCkn}--->

#### MschapV2Id {#datatypes.MschapV2Id}

- **Description**: The MSCHAPv2 identity.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 32

<!---{Extra-Doc:datatypes.MschapV2Id}--->

#### MschapV2Password {#datatypes.MschapV2Password}

- **Description**: The MSCHAPv2 password.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 64

<!---{Extra-Doc:datatypes.MschapV2Password}--->

#### Mtu {#datatypes.Mtu}

- **Description**: The maximum transmission unit type.
- **Type**: integer

<!---{Extra-Doc:datatypes.Mtu}--->

#### NameServers {#datatypes.NameServers}

- **Description**: Name servers type.
- **Type**: array
- **Element type**: [IpAddress](#datatypes.IpAddress)
- **Null Value**: No
- **Minimum item number**: 0
- **Maximum item number**: 3

<!---{Extra-Doc:datatypes.NameServers}--->

#### Peap {#datatypes.Peap}

- **Description**: The PEAP parts of an MSCHAPv2 authentication.
- **Type**: complex
- **Fields**
    - **label**
        - **Description**: The PEAP label to use. Only used if PEAP version is set to 1.
        - **Type**: [PeapLabel](#datatypes.PeapLabel)
        - **Nullable**: No / **Gettable**: No
    - **version**
        - **Description**: The PEAP version to use. If set to 1, the label needs to be set as well.
        - **Type**: [PeapVersion](#datatypes.PeapVersion)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Peap}--->

#### PeapLabel {#datatypes.PeapLabel}

- **Description**: The PEAP label.
- **Type**: string
- **Enum Values**: "CLIENT_EAP_ENCRYPTION", "CLIENT_PEAP_ENCRYPTION"

<!---{Extra-Doc:datatypes.PeapLabel}--->

#### PeapVersion {#datatypes.PeapVersion}

- **Description**: The PEAP version.
- **Type**: integer
- **Minimum Value**: 0
- **Maximum Value**: 1

<!---{Extra-Doc:datatypes.PeapVersion}--->

#### RadioGeneration {#datatypes.RadioGeneration}

- **Description**: The radio generation type.
- **Type**: string
- **Enum Values**: "4G", "5G"

<!---{Extra-Doc:datatypes.RadioGeneration}--->

#### RadioInterface {#datatypes.RadioInterface}

- **Description**: The connected radio interface type.
- **Type**: string
- **Enum Values**: "4G", "5GSA", "5GNSA", "UNKNOWN"

<!---{Extra-Doc:datatypes.RadioInterface}--->

#### RadioParameters {#datatypes.RadioParameters}

- **Description**: The radio parameter object.
- **Type**: complex
- **Fields**
    - **band**
        - **Description**: The radio band class.
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **bandwidth**
        - **Description**: The bandwidth (in MHz).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **downlinkModulationCodingScheme**
        - **Description**: Typically 0-31 for 5G and 0-28 for 4G. Higher value means better signal quality and higher potential data rates.

        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **physicalCellId**
        - **Description**: The physical cell ID.
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **signalStrength**
        - **Description**: The signal strength parameters.
        - **Type**: [SignalStrength](#datatypes.SignalStrength)
        - **Nullable**: No / **Gettable**: No
    - **txPower**
        - **Description**: The transmit power (in dBm/10).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **uplinkModulationCodingScheme**
        - **Description**: Typically 0-31 for 5G and 0-28 for 4G. Higher value means better signal quality and higher potential data rates.

        - **Type**: integer
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.RadioParameters}--->

#### RemoteAddresses {#datatypes.RemoteAddresses}

- **Description**: The remote addresses type.
- **Type**: array
- **Element type**: [MacAddress](#datatypes.MacAddress)
- **Null Value**: No

<!---{Extra-Doc:datatypes.RemoteAddresses}--->

#### ScanRequestData {#datatypes.ScanRequestData}

- **Description**: The scan request parameters.
- **Type**: complex
- **Fields**
    - **refresh**
        - **Description**: Set to true if the scan should be refreshed or set to false if cached values should be used.
        - **Type**: boolean
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.ScanRequestData}--->

#### ScanResponseData {#datatypes.ScanResponseData}

- **Description**: The scan response data.
- **Type**: complex
- **Fields**
    - **results**
        - **Description**: The list of scan results.
        - **Type**: [ScanResults](#datatypes.ScanResults)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.ScanResponseData}--->

#### ScanResult {#datatypes.ScanResult}

- **Description**: The scan result data type.
- **Type**: complex
- **Fields**
    - **band**
        - **Description**: The band of the scanned access point.
        - **Type**: [WlanBand](#datatypes.WlanBand)
        - **Nullable**: No / **Gettable**: No
    - **bssid**
        - **Description**: The BSSID (Basic Service Set Identifier) of the scanned access point.
        - **Type**: [MacAddress](#datatypes.MacAddress)
        - **Nullable**: No / **Gettable**: No
    - **channel**
        - **Description**: The channel of the scanned access point.
        - **Type**: [WlanChannel](#datatypes.WlanChannel)
        - **Nullable**: No / **Gettable**: No
    - **keyMgmt**
        - **Description**: The key managements used by the scanned access point.
        - **Type**: [KeyMgmts](#datatypes.KeyMgmts)
        - **Nullable**: No / **Gettable**: No
    - **signalStrength**
        - **Description**: The signal strength of the scanned access point (in dBm).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **ssid**
        - **Description**: The SSID (Service Set Identifier) of the scanned access point.
        - **Type**: [Ssid](#datatypes.Ssid)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.ScanResult}--->

#### ScanResults {#datatypes.ScanResults}

- **Description**: The scan results.
- **Type**: array
- **Element type**: [ScanResult](#datatypes.ScanResult)
- **Null Value**: No

<!---{Extra-Doc:datatypes.ScanResults}--->

#### SearchDomains {#datatypes.SearchDomains}

- **Description**: Search domains type.
- **Type**: array
- **Element type**: [DomainName](#datatypes.DomainName)
- **Null Value**: No
- **Minimum item number**: 0
- **Maximum item number**: 6

<!---{Extra-Doc:datatypes.SearchDomains}--->

#### SignalStrength {#datatypes.SignalStrength}

- **Description**: The signal strength object.
- **Type**: complex
- **Fields**
    - **rsrp**
        - **Description**: The Reference Signal Received Power (in dBm).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **rsrq**
        - **Description**: The Reference Signal Received Quality (in dB).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **rssi**
        - **Description**: The Received Signal Strength Indicator (in dBm).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **snr**
        - **Description**: The Signal to noise ratio (in dB).
        - **Type**: integer
        - **Nullable**: No / **Gettable**: No
    - **ssi**
        - **Description**: The Signal Strength Index.
        - **Type**: [SignalStrengthIndex](#datatypes.SignalStrengthIndex)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.SignalStrength}--->

#### SignalStrengthIndex {#datatypes.SignalStrengthIndex}

- **Description**: The signal strength index ranging from 0 (weakest) to 4 (strongest), -1 if no signal.
- **Type**: integer
- **Minimum Value**: -1
- **Maximum Value**: 4

<!---{Extra-Doc:datatypes.SignalStrengthIndex}--->

#### SimPin {#datatypes.SimPin}

- **Description**: The SIM PIN type.
- **Type**: string
- **Minimum Length**: 4
- **Maximum Length**: 8
- **Pattern**: ^[0-9]+$

<!---{Extra-Doc:datatypes.SimPin}--->

#### SimPinAndEnable {#datatypes.SimPinAndEnable}

- **Description**: The PIN and PIN enabled object.
- **Type**: complex
- **Fields**
    - **enable**
        - **Description**: true if SIM PIN protection should be enabled, otherwise false.
        - **Type**: boolean
        - **Nullable**: No / **Gettable**: No
    - **pin**
        - **Description**: The SIM PIN.
        - **Type**: [SimPin](#datatypes.SimPin)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.SimPinAndEnable}--->

#### SimPinAndPuk {#datatypes.SimPinAndPuk}

- **Description**: The PIN and PUK object.
- **Type**: complex
- **Fields**
    - **pin**
        - **Description**: The SIM PIN.
        - **Type**: [SimPin](#datatypes.SimPin)
        - **Nullable**: No / **Gettable**: No
    - **puk**
        - **Description**: The PUK code of the SIM.
        - **Type**: [SimPuk](#datatypes.SimPuk)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.SimPinAndPuk}--->

#### SimPinOldAndNew {#datatypes.SimPinOldAndNew}

- **Description**: The old and new PIN object.
- **Type**: complex
- **Fields**
    - **newpin**
        - **Description**: The new SIM PIN.
        - **Type**: [SimPin](#datatypes.SimPin)
        - **Nullable**: No / **Gettable**: No
    - **oldpin**
        - **Description**: The existing SIM PIN.
        - **Type**: [SimPin](#datatypes.SimPin)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.SimPinOldAndNew}--->

#### SimPuk {#datatypes.SimPuk}

- **Description**: The SIM PUK type.
- **Type**: string
- **Minimum Length**: 8
- **Maximum Length**: 8
- **Pattern**: ^[0-9]+$

<!---{Extra-Doc:datatypes.SimPuk}--->

#### SimSlotId {#datatypes.SimSlotId}

- **Description**: A string index from 0 to 99.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 2
- **Pattern**: ^[0-9]+$

<!---{Extra-Doc:datatypes.SimSlotId}--->

#### SimStatus {#datatypes.SimStatus}

- **Description**: The PIN can be disabled, which means the SIM PIN is not required. Enter the SIM PIN to unlock the SIM card and change its status from LOCKED to UNLOCKED. Three incorrect SIM PIN attempts will make the SIM card BLOCKED. Ten incorrect SIM PUK attempts will make the SIM card PERMANENTLY_BLOCKED. A broken SIM card will be presented as UNKNOWN. The UNKNOWN status will also appear when fetching the SimStatus while the modem is not ready.

- **Type**: string
- **Enum Values**: "PIN_DISABLED", "UNLOCKED", "LOCKED", "BLOCKED", "PERMANENTLY_BLOCKED", "NO_SIM", "UNKNOWN"

<!---{Extra-Doc:datatypes.SimStatus}--->

#### Ssid {#datatypes.Ssid}

- **Description**: The SSID (Service Set Identifier) of the WLAN network.
- **Type**: string
- **Minimum Length**: 0
- **Maximum Length**: 32

<!---{Extra-Doc:datatypes.Ssid}--->

#### State {#datatypes.State}

- **Description**: The link state type.
- **Type**: string
- **Enum Values**: "UP", "DOWN"

<!---{Extra-Doc:datatypes.State}--->

#### TcpEcn {#datatypes.TcpEcn}

- **Description**: Determines how the device handles TCP Explicit Congestion Notification (ECN). DISABLED turns off ECN. ACCEPT_ONLY accepts ECN from peers but does not initiate. ACCEPT_INITIATE both accepts and initiates ECN.
- **Type**: string
- **Enum Values**: "DISABLED", "ACCEPT_INITIATE", "ACCEPT_ONLY"

<!---{Extra-Doc:datatypes.TcpEcn}--->

#### TestSettingsRequestData {#datatypes.TestSettingsRequestData}

- **Description**: The test settings request parameters.
- **Type**: complex
- **Fields**
    - **timeout**
        - **Description**: The maximum amount of time (in seconds) the test is allowed to run.
        - **Type**: [TestSettingsTimeout](#datatypes.TestSettingsTimeout)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.TestSettingsRequestData}--->

#### TestSettingsResponseData {#datatypes.TestSettingsResponseData}

- **Description**: The test settings response parameters.
- **Type**: complex
- **Fields**
    - **result**
        - **Description**: The results of the test.
        - **Type**: [TestSettingsResult](#datatypes.TestSettingsResult)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.TestSettingsResponseData}--->

#### TestSettingsResult {#datatypes.TestSettingsResult}

- **Description**: The outcome of a WLAN station settings test..
- **Type**: string
- **Enum Values**: "AUTH_SUCCESSFUL", "AUTH_FAIL", "TIMEOUT", "CONFIG_CHANGED", "INTERRUPTED", "CONFIG_INCOMPLETE"

<!---{Extra-Doc:datatypes.TestSettingsResult}--->

#### TestSettingsTimeout {#datatypes.TestSettingsTimeout}

- **Description**: The maximum duration in seconds for a test settings operation.
- **Type**: integer
- **Minimum Value**: 1
- **Maximum Value**: 30

<!---{Extra-Doc:datatypes.TestSettingsTimeout}--->

#### Uplink {#datatypes.Uplink}

- **Description**: The uplink device type.
- **Type**: string
- **Enum Values**: "NONE", "WIRED", "MODEM", "SFP", "WLAN"

<!---{Extra-Doc:datatypes.Uplink}--->

#### VendorClassIdentifier {#datatypes.VendorClassIdentifier}

- **Description**: The Vendor Class Identifier type.
- **Type**: string
- **Minimum Length**: 0
- **Maximum Length**: 128

<!---{Extra-Doc:datatypes.VendorClassIdentifier}--->

#### VlanId {#datatypes.VlanId}

- **Description**: The ID of the VLAN. Needs to be between 1 and 4094.
- **Type**: string
- **Pattern**: ^([1-9]|[1-9]\\d|[1-9]\\d{2}|[1-3]\\d{3}|40[0-8]\\d|409[0-4])$

<!---{Extra-Doc:datatypes.VlanId}--->

#### WlanBand {#datatypes.WlanBand}

- **Description**: The WLAN band data type.
- **Type**: string
- **Enum Values**: "BAND_2_4GHZ", "BAND_5GHZ"

<!---{Extra-Doc:datatypes.WlanBand}--->

#### WlanChannel {#datatypes.WlanChannel}

- **Description**: The WLAN channel data type.
- **Type**: integer

<!---{Extra-Doc:datatypes.WlanChannel}--->

#### WpaVersion {#datatypes.WpaVersion}

- **Description**: The WPA version type.
- **Type**: string
- **Enum Values**: "NONE", "WPA1", "WPA2", "UNKNOWN"

<!---{Extra-Doc:datatypes.WpaVersion}--->


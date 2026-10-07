# Virtual Host (Version 1.2.0)

## Overview

- **Short Description**: Multiple simultaneous authentication schemes.
- **ID**: virtualhost
- **Version**: 1.2.0
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
virtualhost.v1 (Root Entity)
    ├── create_virtualHost (Action)
    ├── defaulthosts (Entity Collection)
        ├── active (Property)
        ├── auth_type (Property)
        ├── port (Property)
        ├── secure (Property)
        ├── server_name (Property)
    ├── virtualhosts (Entity Collection)
        ├── active (Property)
        ├── auth_type (Property)
        ├── port (Property)
        ├── secure (Property)
        ├── server_name (Property)
```

### Entities

#### virtualhost.v1 {#virtualhost.v1}

- **Description**: Management of web server virtual hosts.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:virtualhost.v1}--->

##### Properties

This entity has no properties.

##### Actions

###### create_virtualHost  {#virtualhost.v1.create_virtualHost}
- **Description**: Create a virtual host.
- **Request Datatype**: [create_virtualhost_input](#datatypes.create_virtualhost_input)
- **Response Datatype**: Empty Object
- **Trigger Permissions**: admin

<!---{Extra-Doc:virtualhost.v1.create_virtualHost}--->

---

#### virtualhost.v1.defaulthosts {#virtualhost.v1.defaulthosts}

- **Description**: Default virtual hosts.
- **Type**: Collection (Key Property: [server_name](#virtualhost.v1.defaulthosts.server_name))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: active, auth_type, port
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts}--->

##### Properties

###### active {#virtualhost.v1.defaulthosts.active}

- **Description**: If host is active or not. Unaccessible if inactive.
- **Datatype**: [boolean_type](#datatypes.boolean_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts.active}--->

###### auth_type {#virtualhost.v1.defaulthosts.auth_type}

- **Description**: The name of the authentication schema.
- **Datatype**: [auth_type_default](#datatypes.auth_type_default)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts.auth_type}--->

###### port {#virtualhost.v1.defaulthosts.port}

- **Description**: The port that the host will use.
- **Datatype**: [port_type](#datatypes.port_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts.port}--->

###### secure {#virtualhost.v1.defaulthosts.secure}

- **Description**: If the host use secure TLS connection or not.
- **Datatype**: [boolean_type](#datatypes.boolean_type)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts.secure}--->

###### server_name {#virtualhost.v1.defaulthosts.server_name}

- **Description**: Server name for host.
- **Datatype**: [server_name_type](#datatypes.server_name_type)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.defaulthosts.server_name}--->

##### Actions

This entity has no actions.

---

#### virtualhost.v1.virtualhosts {#virtualhost.v1.virtualhosts}

- **Description**: Dynamically added virtual hosts.
- **Type**: Collection (Key Property: [server_name](#virtualhost.v1.virtualhosts.server_name))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: active, auth_type, port, secure, server_name
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts}--->

##### Properties

###### active {#virtualhost.v1.virtualhosts.active}

- **Description**: If host is active or not. Unaccessible if inactive.
- **Datatype**: [boolean_type](#datatypes.boolean_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts.active}--->

###### auth_type {#virtualhost.v1.virtualhosts.auth_type}

- **Description**: The name of the authentication schema.
- **Datatype**: [auth_type](#datatypes.auth_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts.auth_type}--->

###### port {#virtualhost.v1.virtualhosts.port}

- **Description**: The port that the host will use.
- **Datatype**: [port_type](#datatypes.port_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts.port}--->

###### secure {#virtualhost.v1.virtualhosts.secure}

- **Description**: If the host use secure TLS connection or not.
- **Datatype**: [boolean_type](#datatypes.boolean_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts.secure}--->

###### server_name {#virtualhost.v1.virtualhosts.server_name}

- **Description**: Server name for host.
- **Datatype**: [server_name_type](#datatypes.server_name_type)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:virtualhost.v1.virtualhosts.server_name}--->

##### Actions

This entity has no actions.

---

### Data Types

#### auth_type {#datatypes.auth_type}

- **Description**: Authentication type.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 32
- **Pattern**: ^(basic|digest|oidc|oauth-ccgrant)$

<!---{Extra-Doc:datatypes.auth_type}--->

#### auth_type_default {#datatypes.auth_type_default}

- **Description**: Authentication type.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 32
- **Pattern**: ^(basic|digest)$

<!---{Extra-Doc:datatypes.auth_type_default}--->

#### boolean_type {#datatypes.boolean_type}

- **Description**: true or false.
- **Type**: boolean

<!---{Extra-Doc:datatypes.boolean_type}--->

#### create_virtualhost_input {#datatypes.create_virtualhost_input}

- **Description**: VirtualHost creation action parameters.
- **Type**: complex
- **Fields**
    - **active**
        - **Description**: Virtual host state, true if active.
        - **Type**: [boolean_type](#datatypes.boolean_type)
        - **Nullable**: No / **Gettable**: No
    - **auth_type**
        - **Description**: Authentication type.
        - **Type**: [auth_type](#datatypes.auth_type)
        - **Nullable**: No / **Gettable**: No
    - **port**
        - **Description**: Port number.
        - **Type**: [port_type](#datatypes.port_type)
        - **Nullable**: No / **Gettable**: No
    - **secure**
        - **Description**: True if secure TLS connection.
        - **Type**: [boolean_type](#datatypes.boolean_type)
        - **Nullable**: No / **Gettable**: No
    - **server_name**
        - **Description**: Server name that vh will be based on.
        - **Type**: [server_name_type](#datatypes.server_name_type)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.create_virtualhost_input}--->

#### port_type {#datatypes.port_type}

- **Description**: Port number.
- **Type**: integer
- **Minimum Value**: 1
- **Maximum Value**: 65535

<!---{Extra-Doc:datatypes.port_type}--->

#### server_name_type {#datatypes.server_name_type}

- **Description**: Client ID type.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 64
- **Pattern**: ^[A-Za-z0-9-.]+$

<!---{Extra-Doc:datatypes.server_name_type}--->


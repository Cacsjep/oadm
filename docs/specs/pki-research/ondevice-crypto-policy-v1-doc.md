# Cryptographic Policy (Version 1.1.0)

## Overview

- **Short Description**: Management of the system wide cryptographic policy
- **ID**: crypto-policy
- **Version**: 1.1.0
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
crypto-policy.v1 (Root Entity)
    ├── active_policy (Property)
    ├── policies (Entity Collection)
        ├── description (Property)
        ├── name (Property)
        ├── nice_name (Property)
```

### Entities

#### crypto-policy.v1 {#crypto-policy.v1}

- **Description**: The cryptographic policy management root object
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:crypto-policy.v1}--->

##### Properties

###### active_policy {#crypto-policy.v1.active_policy}

- **Description**: The cryptographic policy that is active
- **Datatype**: [name](#datatypes.name)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:crypto-policy.v1.active_policy}--->

##### Actions

This entity has no actions.

---

#### crypto-policy.v1.policies {#crypto-policy.v1.policies}

- **Description**: The cryptographic policies
- **Type**: Collection (Key Property: [name](#crypto-policy.v1.policies.name))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:crypto-policy.v1.policies}--->

##### Properties

###### description {#crypto-policy.v1.policies.description}

- **Description**: The policy description
- **Datatype**: [pol_description](#datatypes.pol_description)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:crypto-policy.v1.policies.description}--->

###### name {#crypto-policy.v1.policies.name}

- **Description**: The policy identifier
- **Datatype**: [name](#datatypes.name)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:crypto-policy.v1.policies.name}--->

###### nice_name {#crypto-policy.v1.policies.nice_name}

- **Description**: Human friendly name for the policy
- **Datatype**: [nice_name](#datatypes.nice_name)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:crypto-policy.v1.policies.nice_name}--->

##### Actions

This entity has no actions.

---

### Data Types

#### name {#datatypes.name}

- **Description**: Identifier for a cryptographic policy
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 32

<!---{Extra-Doc:datatypes.name}--->

#### nice_name {#datatypes.nice_name}

- **Description**: A human friendly name for a cryptographic policy
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 128

<!---{Extra-Doc:datatypes.nice_name}--->

#### pol_description {#datatypes.pol_description}

- **Description**: The cryptographic policy description
- **Type**: string
- **Maximum Length**: 1024

<!---{Extra-Doc:datatypes.pol_description}--->


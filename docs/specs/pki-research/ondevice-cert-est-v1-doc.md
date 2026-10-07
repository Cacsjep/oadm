# Enrollment over Secure Transport (Version 1.0.1)

## Overview

- **Short Description**: Automatic certificate enrollment with the EST protocol.
- **ID**: cert-est
- **Version**: 1.0.1
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
cert-est.v1 (Root Entity)
    ├── supportedServices (Property)
    ├── profiles (Entity Collection)
        ├── caCertificates (Property)
        ├── certificate (Property)
        ├── id (Property)
        ├── server (Property)
        ├── services (Property)
        ├── status (Property)
        ├── subject (Property)
        ├── subjectAlternativeNames (Property)
```

### Entities

#### cert-est.v1 {#cert-est.v1}

- **Description**: EST root object
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert-est.v1}--->

##### Properties

###### supportedServices {#cert-est.v1.supportedServices}

- **Description**: List of the services that can have enrolled certificates assigned to it.
- **Datatype**: [ServiceList](#datatypes.ServiceList)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.supportedServices}--->

##### Actions

This entity has no actions.

---

#### cert-est.v1.profiles {#cert-est.v1.profiles}

- **Description**: EST profiles.
- **Type**: Collection (Key Property: [id](#cert-est.v1.profiles.id))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: caCertificates, certificate, id, server, services, subject, subjectAlternativeNames
    - **Add** (**Permissions**: admin)
        - **Required properties**: id
        - **Optional properties**: caCertificates, certificate, server, services, subject, subjectAlternativeNames
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert-est.v1.profiles}--->

##### Properties

###### caCertificates {#cert-est.v1.profiles.caCertificates}

- **Description**: EST server CA certificates
- **Datatype**: [CertificateList](#datatypes.CertificateList)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.caCertificates}--->

###### certificate {#cert-est.v1.profiles.certificate}

- **Description**: Existing certificate to base CSR on
- **Datatype**: [Alias](#datatypes.Alias)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.certificate}--->

###### id {#cert-est.v1.profiles.id}

- **Description**: EST profile identifier.
- **Datatype**: [Alias](#datatypes.Alias)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.id}--->

###### server {#cert-est.v1.profiles.server}

- **Description**: EST server hostname.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.server}--->

###### services {#cert-est.v1.profiles.services}

- **Description**: Services to assign enrolled certificate to
- **Datatype**: [ServiceList](#datatypes.ServiceList)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.services}--->

###### status {#cert-est.v1.profiles.status}

- **Description**: Status of the setup of the EST profile
- **Datatype**: [Status](#datatypes.Status)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.status}--->

###### subject {#cert-est.v1.profiles.subject}

- **Description**: x509 subject of the enrolled certificate
- **Datatype**: [Subject](#datatypes.Subject)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.subject}--->

###### subjectAlternativeNames {#cert-est.v1.profiles.subjectAlternativeNames}

- **Description**: Subject alternative names, each prefixed with either "IP:" or "DNS:", e.g. "DNS:example.org"
- **Datatype**: [SubjectAltNameList](#datatypes.SubjectAltNameList)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert-est.v1.profiles.subjectAlternativeNames}--->

##### Actions

This entity has no actions.

---

### Data Types

#### Alias {#datatypes.Alias}

- **Description**: Client-defined identifier
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 128

<!---{Extra-Doc:datatypes.Alias}--->

#### CertificateList {#datatypes.CertificateList}

- **Description**: Certificates list
- **Type**: array
- **Element type**: [Alias](#datatypes.Alias)
- **Null Value**: No

<!---{Extra-Doc:datatypes.CertificateList}--->

#### CommonName {#datatypes.CommonName}

- **Description**: The Common Name is used to specify the fully qualified domain name (FQDN) of the server or the name of the individual or organization for which the certificate is issued.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 64

<!---{Extra-Doc:datatypes.CommonName}--->

#### Service {#datatypes.Service}

- **Description**: A service to configure with an EST enrolled certificate.
- **Type**: string
- **Enum Values**: "WEBSERVER", "NETAUTH", "MQTT", "RTSPS"

<!---{Extra-Doc:datatypes.Service}--->

#### ServiceList {#datatypes.ServiceList}

- **Description**: Service list
- **Type**: array
- **Element type**: [Service](#datatypes.Service)
- **Null Value**: No

<!---{Extra-Doc:datatypes.ServiceList}--->

#### Status {#datatypes.Status}

- **Description**: The EST profiles status.
- **Type**: string
- **Enum Values**: "UNCONNECTED", "CONNECTED", "ENROLLED"

<!---{Extra-Doc:datatypes.Status}--->

#### Subject {#datatypes.Subject}

- **Description**: Subject of the CSR that will be sent to the EST server. Uses the default subject settings if left empty.
- **Type**: complex
- **Fields**
    - **cn**
        - **Description**: Common Name
        - **Type**: [CommonName](#datatypes.CommonName)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.Subject}--->

#### SubjectAltNameList {#datatypes.SubjectAltNameList}

- **Description**: Subject Alternative Name list
- **Type**: array
- **Element type**: [SubjectAlternativeName](#datatypes.SubjectAlternativeName)
- **Null Value**: No

<!---{Extra-Doc:datatypes.SubjectAltNameList}--->

#### SubjectAlternativeName {#datatypes.SubjectAlternativeName}

- **Description**: The Subject Alternative Name is used to specify additional domain names and IP addresses for which the certificate is issued.
- **Type**: string

<!---{Extra-Doc:datatypes.SubjectAlternativeName}--->


targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module cae 'cae/cae.bicep' = {
  name: 'cae'
  scope: rg
  params: {
    location: location
    userPrincipalId: principalId
  }
}

module storage 'storage/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
  }
}

module webfrontend_identity 'webfrontend-identity/webfrontend-identity.bicep' = {
  name: 'webfrontend-identity'
  scope: rg
  params: {
    location: location
  }
}

module webfrontend_roles_storage 'webfrontend-roles-storage/webfrontend-roles-storage.bicep' = {
  name: 'webfrontend-roles-storage'
  scope: rg
  params: {
    location: location
    storage_outputs_name: storage.outputs.name
    principalId: webfrontend_identity.outputs.principalId
  }
}

output cae_AZURE_CONTAINER_REGISTRY_NAME string = cae.outputs.AZURE_CONTAINER_REGISTRY_NAME

output cae_AZURE_CONTAINER_REGISTRY_ENDPOINT string = cae.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output cae_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = cae.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output cae_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = cae.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output cae_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = cae.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output webfrontend_identity_id string = webfrontend_identity.outputs.id

output storage_blobEndpoint string = storage.outputs.blobEndpoint

output webfrontend_identity_clientId string = webfrontend_identity.outputs.clientId
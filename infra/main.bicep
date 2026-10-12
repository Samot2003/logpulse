// LogPulse on Azure, sized for the free tiers: Azure SQL Database free offer (auto-pauses instead of billing when the
// monthly allowance runs out) and Azure Container Apps on the Consumption profile (scale to zero, one replica max).
// Deployed once by hand into an empty resource group (see docs/deploy-azure.md); the CD workflow only swaps images.

targetScope = 'resourceGroup'

@description('Azure region. Azure for Students only allows some regions: use the resource group\'s.')
param location string = resourceGroup().location

@description('Prefix for every resource name. The SQL server name must be globally unique, so it gets a suffix.')
@maxLength(12)
param namePrefix string = 'logpulse'

// GitHub issues OIDC subjects with immutable ids ("owner@ownerId/repo@repoId"): a repository deleted and created
// again under the same name gets a new id and cannot use this identity. The ids are public (GitHub REST API).
@description('GitHub repository allowed to deploy, as it appears in the OIDC subject: owner@ownerId/name@repoId.')
param githubRepository string = 'Samot2003@114223205/logpulse@1402087600'

@description('Images to start with. The CD workflow replaces them with the sha-tagged ones on every deploy.')
param apiImage string = 'ghcr.io/samot2003/logpulse-api:latest'
param dashboardImage string = 'ghcr.io/samot2003/logpulse-dashboard:latest'

@description('SQL administrator login (also used by the API, which applies the schema scripts).')
param sqlAdminLogin string = 'logpulseadmin'

@secure()
@minLength(16)
param sqlAdminPassword string

@secure()
@minLength(32)
@description('JWT signing key of the API (at least 32 characters).')
param jwtSigningKey string

@secure()
@minLength(12)
param adminPassword string

@secure()
@minLength(12)
param viewerPassword string

// One /27 is the minimum for a workload profiles environment: the ingress, the replicas and nothing else live here.
var vnetAddressPrefix = '10.40.0.0/24'
var appsSubnetPrefix = '10.40.0.0/27'
var sqlServerName = '${namePrefix}-sql-${uniqueString(resourceGroup().id)}'
var databaseName = 'LogPulse'

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            { name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }
          ]
          // Traffic to Azure SQL leaves through the service endpoint, so the SQL firewall can allow this subnet only
          // instead of "every Azure service" (free, unlike a private endpoint).
          serviceEndpoints: [{ service: 'Microsoft.Sql' }]
        }
      }
    ]
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled' // reachable only through the virtual network rule below: no IP firewall rules
  }
}

resource sqlVnetRule 'Microsoft.Sql/servers/virtualNetworkRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'container-apps'
  properties: {
    virtualNetworkSubnetId: vnet.properties.subnets[0].id
  }
}

// Free offer: serverless General Purpose with a monthly allowance. AutoPause stops the database until next month
// instead of billing once the allowance is used up.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: { name: 'GP_S_Gen5_2', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB, the free offer's limit
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// No Log Analytics workspace: it bills per GB ingested. Console logs are still available with
// "az containerapp logs show".
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: vnet.properties.subnets[0].id
      internal: false
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60'

// Both apps scale to zero and never above one replica: the cost ceiling, and what the in-memory dashboard sessions
// and SignalR connections need anyway.
var scale = {
  minReplicas: 0
  maxReplicas: 1
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-api'
  location: location
  // The connection string only references the server: wait for the database and the network rule too.
  dependsOn: [database, sqlVnetRule]
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true // agents send from their own servers
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: [
        { name: 'sql-connection', value: sqlConnectionString }
        { name: 'jwt-signing-key', value: jwtSigningKey }
        { name: 'admin-password', value: adminPassword }
        { name: 'viewer-password', value: viewerPassword }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__LogPulse', secretRef: 'sql-connection' }
            { name: 'Database__CreateIfMissing', value: 'false' }
            { name: 'Jwt__SigningKey', secretRef: 'jwt-signing-key' }
            { name: 'Seed__Users__0__UserName', value: 'admin' }
            { name: 'Seed__Users__0__Password', secretRef: 'admin-password' }
            { name: 'Seed__Users__0__Role', value: 'Admin' }
            { name: 'Seed__Users__1__UserName', value: 'viewer' }
            { name: 'Seed__Users__1__Password', secretRef: 'viewer-password' }
            { name: 'Seed__Users__1__Role', value: 'Viewer' }
            // Requests arrive through the ingress, and the dashboard's through the ingress again: trust X-Forwarded-For
            // from the subnet only, two hops deep. Nothing outside the subnet can reach the container directly.
            { name: 'ForwardedHeaders__KnownNetworks__0', value: appsSubnetPrefix }
            { name: 'ForwardedHeaders__ForwardLimit', value: '2' }
          ]
        }
      ]
      scale: scale
    }
  }
}

resource dashboard 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-dashboard'
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: dashboardImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Dashboard__ApiBaseUrl', value: 'https://${api.properties.configuration.ingress.fqdn}/' }
            { name: 'ForwardedHeaders__KnownNetworks__0', value: appsSubnetPrefix }
          ]
        }
      ]
      scale: scale
    }
  }
}

// Identity for the CD workflow. GitHub gets a token for it through OIDC (no stored password), and only for jobs of
// this repository that run in the "production" environment, which requires a manual approval.
resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-github-deploy'
  location: location
}

resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:production'
    audiences: ['api://AzureADTokenExchange']
  }
}

// A custom role instead of "Contributor": read and update the two container apps, nothing else. Without
// Microsoft.App/containerApps/listSecrets/action it cannot read the SQL password or the JWT key, and it cannot create
// resources. The CD workflow only PATCHes the image of each app's template.
resource deployRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'logpulse-deployer')
  properties: {
    roleName: 'LogPulse deployer (${resourceGroup().name})'
    description: 'Rolls out new images to the LogPulse container apps. Cannot read their secrets.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [
      {
        actions: [
          'Microsoft.App/containerApps/read'
          'Microsoft.App/containerApps/write'
          'Microsoft.App/containerApps/revisions/read'
          // Updating an app requires "join" on its environment (assigned on the environment below). On its own it
          // cannot create apps: that needs containerApps/write on the resource group, which this role never gets.
          'Microsoft.App/managedEnvironments/join/action'
        ]
        notActions: []
      }
    ]
  }
}

var deployRoleId = deployRole.id

resource apiDeployRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: api
  name: guid(api.id, deployIdentity.id, 'logpulse-deployer')
  properties: {
    roleDefinitionId: deployRoleId
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource dashboardDeployRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: dashboard
  name: guid(dashboard.id, deployIdentity.id, 'logpulse-deployer')
  properties: {
    roleDefinitionId: deployRoleId
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource environmentDeployRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: environment
  name: guid(environment.id, deployIdentity.id, 'logpulse-deployer')
  properties: {
    roleDefinitionId: deployRoleId
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Reader on the resource group so the Azure CLI can look the apps up; it cannot read their secrets or change anything.
var readerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')

resource groupReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployIdentity.id, readerRoleId)
  properties: {
    roleDefinitionId: readerRoleId
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output dashboardUrl string = 'https://${dashboard.properties.configuration.ingress.fqdn}'
output azureClientId string = deployIdentity.properties.clientId
output azureTenantId string = tenant().tenantId
output azureSubscriptionId string = subscription().subscriptionId
output resourceGroup string = resourceGroup().name
output apiAppName string = api.name
output dashboardAppName string = dashboard.name

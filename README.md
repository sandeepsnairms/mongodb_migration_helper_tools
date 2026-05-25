# MongoTestTools

A Blazor Server application that provides three tools for MongoDB testing and migration validation: a Change Stream Monitor for real-time change tracking, a multi-threaded Change Stream Generator for load testing, and a Collection Comparer for verifying data consistency between source and target clusters.

## Features

### Change Stream Monitor
- Real-time monitoring of MongoDB Change Streams
- Configurable stream level: Collection, Database, or Cluster
- Collection namespace filter for cluster-level monitoring (track only specific `db.collection` namespaces)
- Per-collection breakdown table with change counts, TPS, and Max TPS
- Live TPS (Transactions Per Second) and changes per minute metrics (refreshed every 5 seconds)
- Support for resume tokens to continue from a specific point
- Tracks total changes processed and monitoring scope
- Connection string masking for security

### Change Stream Generator
- Multi-threaded document generation (1-50 threads)
- Configurable batch operations (1-1000 documents per batch)
- Selectable operations: Insert, Update, and/or Delete
- Random database and collection generation with configurable prefixes and ranges
- Namespace list mode: supply explicit `db.collection` namespaces for targeted generation
- Loop count control for finite execution (1-10,000 iterations)
- Continuous mode for non-stop generation
- Real-time statistics: insert/update/delete counts and operations per second
- Shard key support for testing sharded collections with configurable field name, prefix, and value ranges
- Connection string masking for security

### Collection Comparer
- Compare documents between a source and target MongoDB cluster using sampled document hashes
- Configurable sample size per collection (1-10,000 documents)
- Namespace-based input: specify collections to compare in `db.collection` format
- Parallel collection comparison with configurable worker count (1-32)
- Optional timestamp fields for sorting by most-recent documents and lag detection
- Lag threshold configuration with auto-recheck of mismatched documents
- Manual recheck of mismatches on demand
- Copy results as HTML table or CSV to clipboard
- Real-time progress, mismatch count, max lag, and active worker display
- Connection string masking for security

### General
- Password-based authentication
- Tool selection screen after login
- Clean, responsive UI built with Bootstrap
- Optimized for Azure Container Apps deployment on Linux

## Prerequisites

- .NET 9.0 SDK
- MongoDB instance with replica set enabled (required for Change Streams)
- Docker (for containerization)

## Local Development

1. Restore dependencies:
```powershell
dotnet restore
```

2. Run the application:
```powershell
dotnet run
```

3. Navigate to `https://localhost:5001` or `http://localhost:5000`

## Building Docker Image

Build the Docker image:
```powershell
docker build -t mongo-migration-tools:latest .
```

Run the container locally:
```powershell
docker run -p 8080:8080 mongo-migration-tools:latest
```

## Deploying to Azure Container Apps

### Prerequisites
- Azure CLI installed ([Download here](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli))
- Azure subscription
- No Docker installation required (builds happen in Azure)

### Quick Deployment

1. **Run the deployment script with your ACR name:**
```powershell
.\deploy-to-azure.ps1 -AcrName "myuniqueacrname123"
```

The script will prompt you to enter an application password for authentication.

**With password as parameter:**
```powershell
.\deploy-to-azure.ps1 -AcrName "myuniqueacrname123" -AppPassword "YourSecurePassword123"
```

**Optional parameters:**
```powershell
.\deploy-to-azure.ps1 -AcrName "myuniqueacrname123" -ResourceGroup "my-rg" -Location "westus2" -ContainerAppName "my-app" -ContainerEnvName "my-env" -AppPassword "YourPassword"
```

The script will automatically:
- ✅ Login to Azure
- ✅ Create a resource group
- ✅ Create Azure Container Registry (ACR)
- ✅ Build the Docker image **in Azure** (no local Docker needed)
- ✅ Create Container Apps environment
- ✅ Deploy the application
- ✅ Verify deployment health
- ✅ Display the public URL

### Updating Existing Deployment
**With custom image tag:**
```powershell
.\update-aca-app.ps1 -ResourceGroup "rg-migration-tools" -ContainerAppName "app-migration-tools" -AcrName "myuniqueacrname123" -ImageTag "v2.0"
```

**Update password:**
```powershell
.\update-aca-app.ps1 -ResourceGroup "rg-migration-tools" -ContainerAppName "app-migration-tools" -AcrName "myuniqueacrname123" -AppPassword "NewPassword123"
```powershell
.\update-aca-app.ps1 -ResourceGroup "rg-migration-tools" -ContainerAppName "app-migration-tools" -AcrName "myuniqueacrname123"
```

**With custom image tag:**
```powershell
.\update-aca-app.ps1 -ResourceGroup "rg-migration-tools" -ContainerAppName "app-migration-tools" -AcrName "myuniqueacrname123" -ImageTag "v2.0"
```

The update script will:
- ✅ Build and push new image (if not already in ACR)
- ✅ Update Container App with new image
- ✅ Preserve environment variables and secrets
- ✅ Verify new image is healthy
- ✅ Display the application URL

### Manual Deployment Steps

1. **Login to Azure:**
```powershell
az login
```

2. **Create a resource group:**
```powershell
az group create --name rg-migration-tools --location eastus
```

3. **Create Azure Container Registry (optional):**
```powershell
az acr create --resource-group rg-migration-tools --name <your-acr-name> --sku Basic --admin-enabled true
```

4. **Build and push the image to ACR (builds in Azure, no local Docker required):**
```powershell
az acr build --registry <your-acr-name> --image mongo-migration-tools:latest .
```

5. **Create Azure Container Apps environment:**
```powershell
az containerapp env create --name env-migration-tools --resource-group rg-migration-tools --location eastus
```

6. **Deploy the container app:**
```powershell
az containerapp create `
  --name app-migration-tools `
  --resource-group rg-migration-tools `
  --environment env-migration-tools `
  --image <your-acr-name>.azurecr.io/mongo-migration-tools:latest `
  --target-port 8080 `

```

## Usage

### Login
1. Navigate to the application URL
2. Enter the application password (configured during deployment via APP_PASSWORD environment variable)
3. Click **Login** or press Enter
4. Select which tool you want to use:
   - **Change Stream Monitor** - Monitor real-time changes
   - **Change Stream Generator** - Generate test data with multiple threads
   - **Collection Comparer** - Compare collections between source and target using document hashes

### Change Stream Monitor

1. After selecting the monitor tool, enter:
   - **Connection String**: Your MongoDB connection string (will be masked)
   - **Stream Level**: Choose Collection, Database, or Cluster scope
   - **Database**: Database name to monitor (not required for cluster level)
   - **Collection**: Collection name to monitor (only required for collection level)
   - **Namespace Filter** (cluster level only): Comma-separated `db.collection` namespaces to track
   - **Resume Token** (optional): Resume from a specific point in the change stream
   
2. Click **Start** to begin monitoring

3. View real-time statistics (updated every 5 seconds):
   - Stream level and monitoring scope
   - Total changes processed
   - Changes per minute and TPS (Transactions Per Second)
   - Per-collection breakdown with change counts, TPS, and Max TPS
   
4. Click **Stop** to halt monitoring
5. Use **Logout** to return to the login screen

### Change Stream Generator

1. After selecting the generator tool, configure:

   **Connection Settings** (Required)
   - **Connection String**: Your MongoDB connection string (will be masked)
   - **Database**: Target database name (required)
   - **Collection**: Target collection name (required)
   - **Document Prefix**: Prefix for generated document IDs (optional)

   **Database/Collection Options**
   - **Use Namespace List**: Supply explicit `db.collection` namespaces (overrides database/collection fields); a random namespace is picked per batch
   - **Random Database**: Generate random database names with specified prefix and range
     - Prefix: e.g., "TestDB" → creates TestDB001, TestDB002, etc.
     - Range: Start and end numbers (e.g., 1-200)
   - **Random Collection**: Generate random collection names with specified prefix and range
     - Prefix: e.g., "coll" → creates coll001, coll002, etc.
     - Range: Start and end numbers (e.g., 1-100)

   **Shard Key Options** (for sharded collections)
   - **Enable Shard Key**: Check to add shard key field to generated documents
   - **Shard Key Field**: Field name for the shard key (default: "tenant")
   - **Shard Key Prefix**: Prefix for shard key values (default: "T")
   - **Shard Key Range**: Start and end numbers for shard key values (e.g., 1-100 → T001-T100)

   **Operations** (select at least one)
   - ☑ **Insert Documents**: Create new documents
   - ☑ **Update Documents**: Update 50% of inserted documents
   - ☑ **Delete Documents**: Delete documents after updates

   **Execution Settings**
   - **Thread Count**: Number of parallel threads (1-50, default: 1)
   - **Batch Size**: Documents per batch operation (1-1000, default: 10)
   - **Loop Count**: Number of iterations per thread (1-10,000, default: 1)
     - Disabled when Continuous Mode is enabled
   - **Continuous Mode**: Run indefinitely until stopped
     - When enabled, ignores Loop Count
     - When disabled, each thread processes: batchSize × loopCount operations

2. Click **Start** to begin generation

3. View real-time statistics:
   - Inserted count (if Insert is enabled)
   - Updated count (if Update is enabled)
   - Deleted count (if Delete is enabled)
   - Operations per second
   - Current status

4. Click **Stop** to halt generation
5. Use **Logout** to return to the login screen

### Examples

**Example 1: Simple Insert Test**
- Database: "TestDB"
- Collection: "TestCollection"
- Operations: ✓ Insert Documents
- Thread Count: 10
- Batch Size: 100
- Continuous Mode: Enabled
- Result: 10 threads continuously inserting 100 documents per batch

**Example 2: Full CRUD Test with Random Collections**
- Database: "MyDatabase"
- Random Collection: ✓ Enabled (Prefix: "coll", Range: 1-50)
- Operations: ✓ Insert, ✓ Update, ✓ Delete
- Thread Count: 5
- Batch Size: 20
- Loop Count: 100
- Result: 5 threads, each performing 100 iterations of insert/update/delete across random collections (coll001-coll050)

**Example 3: Sharded Collection Test**
- Database: "ShardedDB"
- Collection: "Orders"
- Enable Shard Key: ✓
- Shard Key Field: "customerId"
- Shard Key Prefix: "CUST"
- Shard Key Range: 1-1000
- Operations: ✓ Insert Documents
- Thread Count: 20
- Batch Size: 50
- Continuous Mode: Enabled
- Result: 20 threads continuously inserting documents with customerId values like CUST0001-CUST1000

### Collection Comparer

1. After selecting the comparer tool, enter:

   **Connection Settings**
   - **Source Connection String**: MongoDB connection string for the source cluster
   - **Target Connection String**: MongoDB connection string for the target cluster

   **Comparison Settings**
   - **Sample Size**: Number of random documents to compare per collection (1-10,000, default: 100)
   - **Namespaces**: Comma-separated list of `db.collection` namespaces to compare
   - **Timestamp Fields** (optional): Comma-separated field names to sort descending (most recent first); lag from UTC is shown for mismatched documents
   - **Collection Parallelism**: Number of collections to compare simultaneously (1-32, default: 4)

   **Recheck Settings**
   - **Lag Threshold (seconds)**: Threshold for considering a mismatch lag-related (default: 300)
   - **Auto Recheck**: Automatically recheck mismatches above the lag threshold after the initial run

2. Click **Start Comparison** to begin

3. View real-time statistics:
   - Progress (processed / total collections)
   - Total mismatches
   - Max lag across all collections
   - Active workers

4. Review the results table:
   - Namespace, source/target document counts, sample size, mismatch count, status, timestamps
   - Expandable error details per collection

5. After completion:
   - Click **Recheck Mismatches** to re-verify only the mismatched documents
   - Use **Copy as HTML** or **Copy as CSV** to export results (with optional "Mismatches only" and "Headers only" filters)

## Architecture

- **Blazor Server**: Provides real-time UI updates using SignalR
- **MongoDB.Driver**: Official MongoDB driver for .NET (v2.28.0)
- **Change Stream Monitor Service**: Singleton service that monitors MongoDB changes in background
- **Change Stream Generator Service**: Singleton service with multi-threaded document generation
- **Collection Comparer Service**: Singleton service for sample-based document hash comparison across clusters
- **Authentication Service**: Scoped service for password-based authentication
- **Docker**: Multi-stage build optimized for production with non-root user

## Configuration

The application uses the following environment variable:
- **APP_PASSWORD**: Required password for application access (set during Azure deployment)

## Notes

- MongoDB Change Streams require a replica set configuration
- Change Stream Monitor supports Collection, Database, and Cluster stream levels with optional namespace filtering at cluster level
- Change Stream Monitor calculates changes per minute and TPS based on a rolling window updated every 5 seconds
- Resume tokens allow you to continue monitoring from a specific point in the change stream
- Change Stream Generator supports selective operations - you can enable just Insert, or any combination of Insert/Update/Delete
- Random database/collection features use prefix + numeric suffix (e.g., "TestDB" + range 1-100 = TestDB001 through TestDB100)
- Shard key values are formatted with zero-padding based on range (e.g., range 1-999 uses 3 digits: T001, range 1-9999 uses 4 digits: T0001)
- Loop count determines total operations: each thread executes batchSize × loopCount operations before stopping (unless Continuous Mode is enabled)
- Collection Comparer uses document hash comparison on a random sample to detect data inconsistencies between clusters
- Collection Comparer supports automatic recheck of mismatched documents after a configurable lag threshold
- The container runs as a non-root user for enhanced security
- Connection strings are masked in the UI for security (shown as •••••)



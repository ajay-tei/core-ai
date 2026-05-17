<#
.SYNOPSIS
  Tests the LM Studio embedding endpoint and verifies the response dimensions.
.EXAMPLE
  .\test-lmstudio-embeddings.ps1
  .\test-lmstudio-embeddings.ps1 -Endpoint "http://10.0.0.172:1234" -Model "text-embedding-nomic-embed-text-v1.5" -ExpectedDimensions 768
#>

param(
    [string]$Endpoint           = "http://10.0.0.172:1234",
    [string]$Model              = "text-embedding-nomic-embed-text-v1.5",
    [int]$ExpectedDimensions    = 768,
    [string]$ApiKey             = "lm-studio"
)

$url = "$Endpoint/v1/embeddings"

$body = @{
    model  = $Model
    input  = @(
        "The quick brown fox jumps over the lazy dog",
        "Semantic search with vector embeddings"
    )
} | ConvertTo-Json

$headers = @{
    "Authorization" = "Bearer $ApiKey"
    "Content-Type"  = "application/json"
}

Write-Host ""
Write-Host "Testing LM Studio Embedding Endpoint" -ForegroundColor Cyan
Write-Host "  URL:    $url"
Write-Host "  Model:  $Model"
Write-Host "  Expect: $ExpectedDimensions dimensions"
Write-Host ""

try {
    $response = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $body -TimeoutSec 30

    $embeddings = $response.data
    if (-not $embeddings -or $embeddings.Count -eq 0) {
        Write-Host 'FAIL: Response contained no embeddings' -ForegroundColor Red
        exit 1
    }

    $dims = $embeddings[0].embedding.Count
    Write-Host "OK: Got $($embeddings.Count) embedding(s)" -ForegroundColor Green
    Write-Host "OK: Dimensions = $dims" -ForegroundColor Green

    if ($dims -ne $ExpectedDimensions) {
        Write-Host "WARN: Expected $ExpectedDimensions dimensions but got $dims" -ForegroundColor Yellow
        Write-Host "      Update EmbeddingDimensions in appsettings.json to: $dims"
    } else {
        Write-Host "OK: Dimensions match expected ($ExpectedDimensions)" -ForegroundColor Green
    }

    # Verify vectors are non-zero
    $vec = $embeddings[0].embedding
    $nonZero = ($vec | Where-Object { $_ -ne 0.0 }).Count
    if ($nonZero -eq 0) {
        Write-Host 'FAIL: All embedding values are zero - model may not be loaded' -ForegroundColor Red
        exit 1
    }
    Write-Host "OK: Vector is non-zero ($nonZero of $dims values)" -ForegroundColor Green

    # Cosine similarity between the two embeddings
    $v1 = $embeddings[0].embedding
    $v2 = $embeddings[1].embedding
    $dot = 0.0
    for ($i = 0; $i -lt $v1.Count; $i++) { $dot += $v1[$i] * $v2[$i] }
    $mag1 = [Math]::Sqrt(($v1 | ForEach-Object { $_ * $_ } | Measure-Object -Sum).Sum)
    $mag2 = [Math]::Sqrt(($v2 | ForEach-Object { $_ * $_ } | Measure-Object -Sum).Sum)
    $cosine = [Math]::Round($dot / ($mag1 * $mag2), 4)
    Write-Host "OK: Cosine similarity between 2 test inputs = $cosine  (0=unrelated, 1=identical)" -ForegroundColor Green

    Write-Host ""
    Write-Host 'SUCCESS - LM Studio embedding is working' -ForegroundColor Green
    Write-Host ""
    Write-Host 'Recommended appsettings.json RAG section:' -ForegroundColor Cyan
    $config = [ordered]@{
        Enabled             = $false
        QdrantUrl           = "http://qdrant:6333"
        QdrantGrpcUrl       = "http://qdrant:6334"
        EmbeddingProvider   = "OpenAI"
        EmbeddingModel      = $Model
        EmbeddingEndpoint   = $Endpoint
        EmbeddingApiKey     = $ApiKey
        EmbeddingDimensions = $dims
        CollectionName      = "diva_knowledge"
    }
    Write-Host ($config | ConvertTo-Json -Depth 2)

} catch [System.Net.WebException] {
    Write-Host "FAIL: Connection error - $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '      Is LM Studio running and the model loaded?'
    exit 1
} catch {
    Write-Host "FAIL: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Host "      Response: $($reader.ReadToEnd())"
    }
    exit 1
}

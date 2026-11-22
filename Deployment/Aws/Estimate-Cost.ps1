[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateRange(0.000001, 100)][decimal]$Ec2UsdPerHour,
    [Parameter(Mandatory = $true)][ValidateRange(0.000001, 100)][decimal]$Gp3UsdPerGbMonth,
    [ValidateRange(0.01, 744)][decimal]$RunningHours = 2,
    [ValidateRange(0.01, 744)][decimal]$DiskRetainedHours = 2,
    [ValidateRange(0, 100000)][decimal]$OutboundGb = 0,
    [ValidateRange(0, 100)][decimal]$OutboundUsdPerGb = 0
)
$ErrorActionPreference = 'Stop'
if ($DiskRetainedHours -lt $RunningHours) { throw 'DiskRetainedHours must include all running hours.' }
if ($OutboundGb -gt 0 -and $OutboundUsdPerGb -le 0) { throw 'Provide the effective outbound rate; do not assume a free allowance.' }
# Supply current EC2, EBS, and transfer rates for the deployment Region.
$ec2 = $Ec2UsdPerHour * $RunningHours
$ipv4 = [decimal]0.005 * $RunningHours
$disk = 20 * $Gp3UsdPerGbMonth * $DiskRetainedHours / 730
$transfer = $OutboundGb * $OutboundUsdPerGb
[pscustomobject]@{
    EC2_USD = [Math]::Round($ec2, 4)
    IPv4_USD = [Math]::Round($ipv4, 4)
    EBS_Approx_USD = [Math]::Round($disk, 4)
    Outbound_USD = [Math]::Round($transfer, 4)
    TotalBeforeTax_Approx_USD = [Math]::Round($ec2 + $ipv4 + $disk + $transfer, 4)
    Note = 'Planning estimate, not a charge cap. Uses 730-hour month approximation. No credits, free tier, tax or currency conversion.'
}

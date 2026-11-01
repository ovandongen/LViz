namespace LViz.Core.GitHub;

/// <summary>
/// The first half of the OAuth device flow: what github.com answered
/// <c>POST /login/device/code</c> with. Show <see cref="UserCode"/> to the
/// user, open <see cref="VerificationUri"/> in the browser, then poll with
/// <see cref="DeviceCode"/>.
/// </summary>
/// <param name="DeviceCode">Opaque code for the token polling request.</param>
/// <param name="UserCode">Short human-readable code to type on github.com.</param>
/// <param name="VerificationUri">URL the user opens to enter the code.</param>
/// <param name="ExpiresIn">How long the code stays valid.</param>
/// <param name="Interval">How long to wait between polling attempts.</param>
public sealed record GitHubDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

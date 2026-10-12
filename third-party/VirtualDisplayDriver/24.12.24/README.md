# Virtual Display Driver 24.12.24 (signed, x64)

Optional component of the RemoteHub Agent: a virtual monitor for devices without a monitor (headless servers), so the
remote desktop can use the size of the RemoteHub window instead of the 640×480 a GPU without a monitor offers.

- Project: https://github.com/VirtualDrivers/Virtual-Display-Driver (MIT licence, see `LICENSE.txt`)
- Release: 25.5.2, asset `Signed-Driver-v24.12.24-x64.zip` (134,079 bytes,
  SHA-256 `F93E7CE3D640C83C419B1B72D2C83B2D8E34D83E8B7BA11F3328A40D2DA82FA4`)
- Driver version (INF): 11.30.4.434, 12/24/2024 – an Indirect Display Driver (IddCx, user mode / UMDF)
- Signed by: SignPath Foundation (GlobalSign GCC R45 CodeSigning CA 2020), timestamped; certificate valid until 2027-09-07,
  thumbprint `3CF8CF26D8BA266C3A483AB7D26D4A818E317D76` (`SignPathFoundation.cer` is that certificate, public part only)

The files are used unchanged. The agent checks these SHA-256 hashes before it installs anything
(`VirtualDisplay.Files` in `src/RemoteHub.Agent/Desktop/VirtualDisplay.cs`):

| File | SHA-256 |
|---|---|
| MttVDD.inf | 550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D |
| MttVDD.dll | C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05 |
| mttvdd.cat | 08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6 |
| SignPathFoundation.cer | 69DFD3E1CBD49F039463EDFE4C0D876C1DC7FDCD3AEF08700A41355DC6C15FDB |

Updating: put a new release in a new folder, check its signature, update the hashes in `VirtualDisplay.cs` and the path
in `RemoteHub.Agent.csproj`, and test it in the lab (Windows 11 and Server 2025) before it ships.

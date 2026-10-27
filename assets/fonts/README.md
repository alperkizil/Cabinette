# Bundled fonts

The fonts Settings › Appearance › Font offers beyond the ones Windows has (SPEC.md section 11). Each folder holds the five
weights Cabinette uses (Light, Regular, Medium, SemiBold, Bold) exactly as released, unmodified and not subset, and the
license that came with them. All five are under the SIL Open Font License 1.1.

The app embeds the `.ttf` files as WPF resources (`Assets/Fonts/<folder>/`, see `src/Cabinette.App/Cabinette.App.csproj`),
so they never appear as loose files. The OFL requires the license to travel with the font: each license is copied next to
Cabinette.exe as `licenses\<folder>-OFL.txt` and installed with it.

Source Sans 3's license names "Source" as a Reserved Font Name: never modify or subset those files.

| Folder | Font | Version | Source |
|---|---|---|---|
| `Inter` | Inter | 4.1 | https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip (33,707,794 bytes, SHA-256 `9883FDD4A49D4FB66BD8177BA6625EF9A64AA45899767DDE3D36AA425756B11E`), `extras/ttf/` |
| `SourceSans3` | Source Sans 3 | 3.052R | https://github.com/adobe-fonts/source-sans/releases/download/3.052R/TTF-source-sans-3.052R.zip (2,478,353 bytes, SHA-256 `1B0DD1EC44B39F1DD98BBD153A1A3815F083639874DDEE02C842BD601BAD3D21`) |
| `AtkinsonHyperlegibleNext` | Atkinson Hyperlegible Next | commit `7925f50f649b3813257faf2f4c0b381011f434f1` | https://github.com/googlefonts/atkinson-hyperlegible-next, `fonts/ttf/` |
| `Lexend` | Lexend | commit `7894f02b2e7eabc48595f1d4eff3b17b48c6e651` | https://github.com/googlefonts/lexend, `fonts/lexend/ttf/` |
| `JetBrainsMono` | JetBrains Mono | 2.304 | https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip (5,622,857 bytes, SHA-256 `6F6376C6ED2960EA8A963CD7387EC9D76E3F629125BC33D1FDCD7EB7012F7BBF`), `fonts/ttf/` |

## SHA-256 of each file

```
Inter/Inter-Light.ttf                                    164414F0AACBE98A7E64ADDC43F7B3BFD2E32F7B90E101FEEAB227F14C371BDA
Inter/Inter-Regular.ttf                                  40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82
Inter/Inter-Medium.ttf                                   97AD806F526E41546D46365BB3A393145F75B7B1568913DB74549AD8B8DBA872
Inter/Inter-SemiBold.ttf                                 78A843FADE9D4612A5567302FB595B56976EB5FCEBF4FEA5A5912D638BAFCDE3
Inter/Inter-Bold.ttf                                     288316099B1E0A47A4716D159098005EEF7C0066921F34E3200393DBDB01947F
Inter/LICENSE.txt                                        262481E844521B326F5ECD053E59B98C8B2DA78C8EE1BDBB6E8174305E54935A
SourceSans3/SourceSans3-Light.ttf                        FCC10D83A12844E7DC699B1BB85F65F0D1BB0179DE216FEB5D28F806500F7CEF
SourceSans3/SourceSans3-Regular.ttf                      4644C81B86EC9CAAA76B634889968ED3C4F4F52F054855933ACC7C2B21E53B0F
SourceSans3/SourceSans3-Medium.ttf                       A7AF35C1CAB5DD0B29D742F86F2EDFDF83FA7EC213643E765C5C51A2DC24EDBC
SourceSans3/SourceSans3-Semibold.ttf                     A3F4F8DCF343A8F24DC61951DE93F3BA1558B15CD250BA24AF8A40E957081B7D
SourceSans3/SourceSans3-Bold.ttf                         9214B9D95E4231C609802815C2646C98174E2102D0D37F88978A7F8E71006E6A
SourceSans3/LICENSE.md                                   89AD2C4F66DD29127527493E729C31E731F111CF10FAF5774C3DB9275ED0C22C
AtkinsonHyperlegibleNext/AtkinsonHyperlegibleNext-Light.ttf     17FC9907FA40CB0401CEDE44B07B1983AF0D6F7FA4AB2C6A30560C35B0F376A1
AtkinsonHyperlegibleNext/AtkinsonHyperlegibleNext-Regular.ttf   88ED5C31A71584C7772963B02D04BEF1EB7E3D2E9C8B9CB204339B1F82CF432C
AtkinsonHyperlegibleNext/AtkinsonHyperlegibleNext-Medium.ttf    DD50B08B3C560846097D23BAAAF6A97FFA20DD077115D23C59DF68083B9EA05E
AtkinsonHyperlegibleNext/AtkinsonHyperlegibleNext-SemiBold.ttf  8E2FFF779F35AC25239656BB0F3B2BD35959A2009E3DD21A12141C3B9030362B
AtkinsonHyperlegibleNext/AtkinsonHyperlegibleNext-Bold.ttf      994414047DF66BB4998D01C1CB1EEB4A2DDC4622D1AA56BBB8ADBECA7645B041
AtkinsonHyperlegibleNext/OFL.txt                         ACA6A428580965D2297D1B718042DD427C2A9443ECE3B0D02D758E161E0C4030
AtkinsonHyperlegibleNext/AUTHORS.txt                     9C0280F1393DF94E4BEBC255A4826FF7BC90289C467A21FA298492C6F2A76055
Lexend/Lexend-Light.ttf                                  07BF6DCB02FED0E414B268695C54089B9081AAE30887DA49FEC61A60942CA567
Lexend/Lexend-Regular.ttf                                E2082C28389E9871D5D77AE9163D5C0C1C259FE39BDAC91F73D56A600B61F60F
Lexend/Lexend-Medium.ttf                                 AC109D3848FF23F1DDA6CDB2F5E5BDB335811967FC4FB60B5F7B9DF5A301C5BA
Lexend/Lexend-SemiBold.ttf                               204875848CC7981290A75F8BE0E64B335B6D8F6EC6CC3EA05D907A37ADAB8B7B
Lexend/Lexend-Bold.ttf                                   4509838ACC21F2C066E9874C9C5EB52B9B8CDAC1771A9C1FE9E7E2CDA41CE082
Lexend/OFL.txt                                           5DA8505887D0FA7FE963445FD58852707FDA34ADFEB65AF25C99D152BAB285BD
Lexend/AUTHORS.txt                                       011445A889247C239892C3BF3E173CE90D082228CB3F7BC48AB2A0C86A0FD7E5
JetBrainsMono/JetBrainsMono-Light.ttf                    60C18D7DD58D81B3BBD12E8CE32744A8771BFE2B5280574082B0EAED46C60D24
JetBrainsMono/JetBrainsMono-Regular.ttf                  A0BF60EF0F83C5ED4D7A75D45838548B1F6873372DFAC88F71804491898D138F
JetBrainsMono/JetBrainsMono-Medium.ttf                   31C92D01A8A08528B718A43ADDF0AD3DF0AF2CA4B7B3290A452F70F358E14D3D
JetBrainsMono/JetBrainsMono-SemiBold.ttf                 1B3BFA1ED5665A4CE3F9FEB68D2D4E40E70BF8B4B7D9A3EDD418F321B4E166A0
JetBrainsMono/JetBrainsMono-Bold.ttf                     5590990C82E097397517F275F430AF4546E1C45CFF408BDE4255DAD142479DCB
JetBrainsMono/OFL.txt                                    30F0C136E3C88E422D0791ACD97238870F9054A9729BC34CF2FF0D4ED8CAC4AD
JetBrainsMono/AUTHORS.txt                                33F99C94EF0A0909D7584A5D4BDE1FB9B1630DAA620F5A788C28C43327E79412
```

`FontLookTests` checks that every weight resolves to its own file, with no simulated bold.

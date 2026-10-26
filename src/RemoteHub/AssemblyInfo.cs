using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("RemoteHub.Tests")]
// The lab harness removes the broker connection (and its key) it made for a run (LabBroker.cs).
[assembly: InternalsVisibleTo("RemoteHub.LabHarness")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

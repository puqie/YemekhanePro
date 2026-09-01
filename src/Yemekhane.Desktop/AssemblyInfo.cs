using System.Runtime.CompilerServices;
using System.Windows;

// Drawer'in gecikmis MoveFocus(First) cagrisinin kac kez CALISTIGINI (yalnizca
// son odak hedefini degil) dogrudan gozlemlemek icin: iki ayri acilisin
// MoveFocus cagrisi ayni nihai elemani hedefleyebilir, bu durumda WPF zaten
// odakli bir elemana tekrar odaklanildiginda GotFocus'u tekrar raise etmez --
// bu da "kac kez calisti" testini kara-kutu (public API) yollarla imkansiz
// kilar. internal bir sayac/olay bu yuzden gerekli.
[assembly: InternalsVisibleTo("Yemekhane.UnitTests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

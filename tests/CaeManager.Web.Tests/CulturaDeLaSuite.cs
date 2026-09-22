using System.Globalization;
using System.Runtime.CompilerServices;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cultura por defecto de toda la suite: es-ES, la misma que fija Program.cs
/// como fallback del proceso. Las pantallas localizadas formatean y buscan
/// sus textos con la cultura en curso (la resuelve RequestLocalization desde
/// la cuenta); sin esto, el resultado de un test dependería de la cultura de
/// la máquina —invariante en el runner de CI—. Se fija aquí una sola vez, no
/// en cada clase. Un test que necesite otra cultura la pone en su hilo con
/// <c>CultureInfo.CurrentCulture</c>/<c>CurrentUICulture</c> y la restaura.
/// </summary>
internal static class CulturaDeLaSuite
{
#pragma warning disable CA2255 // Es un ensamblado de tests, no una biblioteca: el inicializador es justo lo que se quiere.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void FijarEspanol()
    {
        var espanol = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.DefaultThreadCurrentCulture = espanol;
        CultureInfo.DefaultThreadCurrentUICulture = espanol;
    }
}

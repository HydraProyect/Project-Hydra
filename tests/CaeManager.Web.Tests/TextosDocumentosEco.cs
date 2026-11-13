using CaeManager.Web.Features.Documentos.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Tests;

/// <summary>Localizador de Documentos que devuelve la clave: los textos de los endpoints no son objeto de estos tests.</summary>
internal sealed class TextosDocumentosEco : IStringLocalizer<TextosDocumentos>
{
    public LocalizedString this[string name] => new(name, name);
    public LocalizedString this[string name, params object[] arguments] => new(name, name);
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}

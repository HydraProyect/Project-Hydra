using System.Globalization;
using System.Reflection;
using System.Resources;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Claves compuestas de TextosIncorporacionCartera: <c>EstadoX</c> por cada
/// estado de la solicitud y <c>ErrorX</c> por cada código
/// <c>SolicitudCartera.X</c>.
///
/// <para>
/// <b>Lo que SÍ observa:</b> que cada valor del enum y cada código público de
/// <see cref="ErroresSolicitudCartera"/> tiene su clave en el recurso neutral
/// (es) y en el ca-ES, sin caer al padre. <b>No ve</b> un código de error
/// construido fuera de esa clase; ese caería al genérico, que es lo previsto.
/// </para>
///
/// <para>
/// Existe porque el cruce literal de claves
/// (LocalizacionRecursosYRegistroTests) no ve una clave compuesta: sin esto,
/// un estado o un código nuevo sin texto pintaría la clave o el genérico sin
/// que nada se pusiera en rojo.
/// </para>
/// </summary>
public class IncorporacionCarteraRecursosTests
{
    private static readonly ResourceManager Recursos = new(typeof(TextosIncorporacionCartera));

    public static TheoryData<string> Culturas => new() { "", "ca-ES" };

    [Theory]
    [MemberData(nameof(Culturas))]
    public void Cada_estado_de_la_solicitud_tiene_su_texto(string cultura)
    {
        var textos = Recurso(cultura);

        var faltan = Enum.GetValues<EstadoSolicitudIncorporacionCartera>()
            .Select(TextosIncorporacionCartera.ClaveDeEstado)
            .Where(clave => string.IsNullOrWhiteSpace(textos.GetString(clave)))
            .ToList();

        faltan.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Culturas))]
    public void Cada_codigo_de_error_de_la_solicitud_tiene_su_texto(string cultura)
    {
        var textos = Recurso(cultura);
        var errores = typeof(ErroresSolicitudCartera)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Error))
            .Select(f => (Error)f.GetValue(null)!)
            .ToList();

        errores.Should().NotBeEmpty("si la reflexión no encuentra códigos, el test no comprueba nada");
        errores.Should().OnlyContain(e => e.Codigo.StartsWith("SolicitudCartera.", StringComparison.Ordinal));

        var faltan = errores
            .Select(TextosIncorporacionCartera.ClaveDeError)
            .Where(clave => string.IsNullOrWhiteSpace(textos.GetString(clave)))
            .ToList();

        faltan.Should().BeEmpty();
    }

    // Sin tryParents: una clave que solo esté en el neutral no cuenta para ca-ES.
    private static ResourceSet Recurso(string cultura) =>
        Recursos.GetResourceSet(CultureInfo.GetCultureInfo(cultura), createIfNotExists: true, tryParents: false)
        ?? throw new InvalidOperationException($"No hay recurso para la cultura «{cultura}».");
}

using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.FileStorage;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El tipo concreto <see cref="DiskFileStorageService"/> no se referencia fuera de su registro de dependencias.</b>
///
/// <para>
/// Implementa dos puertos: <c>IFileStorageService</c> (el almacenamiento del Tenant activo) y
/// <c>IAlmacenOriginalesInbound</c> (la única lectura que cruza Tenants, acotada a los handlers de
/// <c>ConsumidoresDelLectorInboundAcotadosTests</c>). El contenedor registra el concreto para que los dos puertos compartan
/// instancia, así que cualquier clase que lo pidiera por su tipo concreto tendría <c>AbrirOriginalAsync</c> sin pasar por
/// el escaneo de consumidores del puerto, y ningún test de comportamiento lo vería.
/// </para>
///
/// <para>
/// Dos instrumentos, porque cada uno ve algo que el otro no: los identificadores de C# de <c>src</c> (Roslyn, sin
/// comentarios ni literales; ve el uso dentro de un cuerpo de método, como <c>GetRequiredService&lt;…&gt;()</c>), y la
/// reflexión sobre constructores, campos y propiedades de Application, Web e Infrastructure (ve una inyección aunque el
/// tipo se escriba con otro alias). Fuera de <c>src</c>, los tests construyen el concreto a propósito.
/// </para>
/// </summary>
public class AlmacenDeDiscoSoloPorSusPuertosTests
{
    private static readonly string[] FicherosAutorizados =
    [
        // El propio tipo.
        "src/CaeManager.Infrastructure/FileStorage/DiskFileStorageService.cs",
        // Su registro: el concreto y los dos puertos que resuelven la misma instancia.
        "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs",
    ];

    [Fact]
    public void Ningun_fichero_de_src_nombra_el_tipo_concreto_fuera_de_su_registro()
    {
        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(DiskFileStorageService)))
            .Select(f => f.Key)
            .ToList();

        ficheros.Should().BeEquivalentTo(FicherosAutorizados,
            "quien pida DiskFileStorageService por su tipo concreto tiene AbrirOriginalAsync, que lee binarios de otro " +
            "Tenant, sin pasar por la lista de consumidores autorizados de IAlmacenOriginalesInbound; se pide por sus puertos");
    }

    [Fact]
    public void Ningun_tipo_inyecta_el_concreto()
    {
        var consumidores = new[] { "CaeManager.Application", "CaeManager.Web", "CaeManager.Infrastructure" }
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(DiskFileStorageService)))
            .Where(t => t.DeclaringType != typeof(DiskFileStorageService))
            .Select(t => t.FullName)
            .ToList();

        consumidores.Should().BeEmpty("el concreto se inyecta por IFileStorageService o IAlmacenOriginalesInbound, nunca por sí mismo");
    }

    [Fact]
    public void Control_los_dos_instrumentos_ven_su_registro()
    {
        // Si el registro dejara de nombrar el concreto, la lista se quedaría muerta y el primer test pasaría por vacío.
        FuentesDeSrc.Analisis[FicherosAutorizados[1]].Palabras.Should().ContainKey(nameof(DiskFileStorageService));
        typeof(DiskFileStorageService).GetInterfaces().Select(i => i.Name)
            .Should().Contain(["IFileStorageService", "IAlmacenOriginalesInbound"]);
    }
}

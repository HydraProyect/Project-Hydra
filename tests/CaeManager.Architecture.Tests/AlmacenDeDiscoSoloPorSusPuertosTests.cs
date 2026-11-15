using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Inbound.FileStorage;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El tipo concreto <see cref="DiskFileStorageService"/> no se referencia fuera de su registro de dependencias, y su
/// lectura entre Tenants solo la consume el adaptador de Inbound.</b>
///
/// <para>
/// Implementa dos puertos del Núcleo: <c>IFileStorageService</c> (el almacenamiento del Tenant activo) y
/// <see cref="IAlmacenArchivosDelTenantPropietario"/> (la única lectura que cruza Tenants). Desde ADR-013 M2 (K-3) ya no
/// implementa <see cref="IAlmacenOriginalesInbound"/>: lo implementa <see cref="AdaptadorAlmacenOriginalesInbound"/>, en el
/// módulo Inbound, sobre la lectura entre Tenants. El contenedor registra el concreto para que los dos puertos compartan
/// instancia, así que cualquier clase que lo pidiera por su tipo concreto, o que inyectara la lectura entre Tenants, abriría
/// archivos de otro Tenant sin pasar por el escaneo de consumidores de <c>ConsumidoresDelLectorInboundAcotadosTests</c>, y
/// ningún test de comportamiento lo vería.
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
    private const string Registro = "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs";
    private const string Adaptador = "src/CaeManager.Infrastructure/Inbound/FileStorage/AdaptadorAlmacenOriginalesInbound.cs";

    private static readonly string[] FicherosAutorizados =
    [
        // El propio tipo.
        "src/CaeManager.Infrastructure/FileStorage/DiskFileStorageService.cs",
        // Su registro: el concreto y los dos puertos que resuelven la misma instancia.
        Registro,
    ];

    /// <summary>Quién nombra la lectura entre Tenants en <c>src</c>: su definición, quien la implementa, su único consumidor y su registro.</summary>
    private static readonly string[] FicherosQueNombranLaLecturaEntreTenants =
    [
        "src/CaeManager.Application/Common/Archivos/IAlmacenArchivosDelTenantPropietario.cs",
        "src/CaeManager.Infrastructure/FileStorage/DiskFileStorageService.cs",
        Adaptador,
        Registro,
    ];

    private static readonly string[] Ensamblados = ["CaeManager.Application", "CaeManager.Web", "CaeManager.Infrastructure"];

    [Fact]
    public void Ningun_fichero_de_src_nombra_el_tipo_concreto_fuera_de_su_registro()
    {
        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(DiskFileStorageService)))
            .Select(f => f.Key)
            .ToList();

        ficheros.Should().BeEquivalentTo(FicherosAutorizados,
            "quien pida DiskFileStorageService por su tipo concreto tiene AbrirDelTenantPropietarioAsync, que lee binarios de " +
            "otro Tenant, sin pasar por la lista de consumidores autorizados; se pide por sus puertos");
    }

    [Fact]
    public void Ningun_tipo_inyecta_el_concreto()
    {
        var consumidores = Ensamblados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(DiskFileStorageService)))
            .Where(t => t.DeclaringType != typeof(DiskFileStorageService))
            .Select(t => t.FullName)
            .ToList();

        consumidores.Should().BeEmpty("el concreto se inyecta por IFileStorageService o IAlmacenArchivosDelTenantPropietario, nunca por sí mismo");
    }

    /// <summary>
    /// ADR-013 M2 (K-3): la lectura entre Tenants del Núcleo solo la inyecta el adaptador de Inbound, que antes exige que la
    /// referencia sea de la carpeta del Tenant que devolvió la base. Otro consumidor abriría cualquier archivo de cualquier
    /// Tenant con su clave y su Tenant, sin la autoridad de la lectura Inbound.
    /// </summary>
    [Fact]
    public void Solo_el_adaptador_de_Inbound_inyecta_la_lectura_entre_Tenants()
    {
        var consumidores = Ensamblados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(IAlmacenArchivosDelTenantPropietario)))
            .Select(Exterior)
            .Distinct()
            .ToList();

        consumidores.Should().BeEquivalentTo([typeof(AdaptadorAlmacenOriginalesInbound)],
            "abrir con la carpeta y la clave de otro Tenant solo lo hace el adaptador de los originales Inbound, después de " +
            "comprobar la referencia que devolvió app_inbound_archivo_autorizado");

        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IAlmacenArchivosDelTenantPropietario)))
            .Select(f => f.Key)
            .ToList();
        ficheros.Should().BeEquivalentTo(FicherosQueNombranLaLecturaEntreTenants,
            "un GetRequiredService<IAlmacenArchivosDelTenantPropietario>() en un cuerpo de método no lo ve la reflexión");
        FuentesDeSrc.Analisis[Registro].Palabras[nameof(IAlmacenArchivosDelTenantPropietario)]
            .Should().Be(1, "la composición la registra una vez, resolviendo el concreto, y no la pide para nada más");
    }

    /// <summary>
    /// ADR-013 M2 (K-3): el almacén de disco del Núcleo ya no implementa el puerto de Inbound; lo implementa solo el
    /// adaptador, y solo la composición lo registra (Program.cs incluido en el barrido de <c>src</c>).
    /// </summary>
    [Fact]
    public void El_puerto_de_originales_Inbound_lo_implementa_solo_el_adaptador_del_modulo_Inbound()
    {
        typeof(DiskFileStorageService).GetInterfaces().Should().NotContain(typeof(IAlmacenOriginalesInbound),
            "el Núcleo no implementa puertos de Inbound (ADR-013 § 4.1, regla 3)");

        var implementaciones = Ensamblados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(ReflexionArquitecturaHelper.TiposDe)
            .Where(t => t is { IsInterface: false } && typeof(IAlmacenOriginalesInbound).IsAssignableFrom(t))
            .ToList();
        implementaciones.Should().BeEquivalentTo([typeof(AdaptadorAlmacenOriginalesInbound)]);
        typeof(AdaptadorAlmacenOriginalesInbound).Namespace.Should().StartWith("CaeManager.Infrastructure.Inbound.");

        var fuera = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IAlmacenOriginalesInbound)))
            .Select(f => f.Key)
            .Where(f => !f.StartsWith("src/CaeManager.Application/Inbound/", StringComparison.Ordinal))
            .ToList();
        fuera.Should().BeEquivalentTo([Adaptador, Registro],
            "fuera de Application Inbound, solo el adaptador lo implementa y solo la composición de Infrastructure lo registra");
        FuentesDeSrc.Analisis[Registro].Palabras[nameof(IAlmacenOriginalesInbound)].Should().Be(1, "un solo registro");
    }

    [Fact]
    public void Control_los_dos_instrumentos_ven_su_registro()
    {
        // Si el registro dejara de nombrar el concreto, la lista se quedaría muerta y el primer test pasaría por vacío.
        FuentesDeSrc.Analisis[FicherosAutorizados[1]].Palabras.Should().ContainKey(nameof(DiskFileStorageService));
        typeof(DiskFileStorageService).GetInterfaces().Select(i => i.Name)
            .Should().Contain(["IFileStorageService", nameof(IAlmacenArchivosDelTenantPropietario)]);
        // Y el escaneo por reflexión ve a un consumidor por su constructor primario: sin esto, el de la lectura entre
        // Tenants pasaría igual si el helper dejara de ver constructores.
        typeof(AdaptadorAlmacenOriginalesInbound).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IAlmacenArchivosDelTenantPropietario));
    }

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}

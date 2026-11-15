using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Inbound.FileStorage;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El tipo concreto <see cref="DiskFileStorageService"/> no se referencia fuera de su registro y de su lectura entre
/// Tenants, y esa lectura solo la consume el adaptador de Inbound.</b>
///
/// <para>
/// <see cref="DiskFileStorageService"/> implementa solo <see cref="IFileStorageService"/> (el almacenamiento del Tenant
/// activo). La única lectura que cruza Tenants es un método <c>internal</c> suyo que expone
/// <see cref="LecturaDeDiscoDelTenantPropietario"/> como <see cref="IAlmacenArchivosDelTenantPropietario"/>, en otra
/// instancia: quien tenga el almacén por <see cref="IFileStorageService"/> no la alcanza con un cast ni con
/// <c>dynamic</c>. Desde ADR-013 M2 (K-3), <see cref="IAlmacenOriginalesInbound"/> lo implementa
/// <see cref="AdaptadorAlmacenOriginalesInbound"/>, en el módulo Inbound, sobre esa lectura. Cualquier clase que pidiera el
/// concreto o la lectura entre Tenants abriría archivos de otro Tenant sin pasar por el escaneo de consumidores de
/// <c>ConsumidoresDelLectorInboundAcotadosTests</c>, y ningún test de comportamiento lo vería.
/// </para>
///
/// <para>
/// Tres instrumentos, porque cada uno ve algo que los otros no: los identificadores de C# de <c>src</c> (Roslyn, sin
/// comentarios ni literales; ven el uso en un cuerpo de método y en Program.cs), la reflexión sobre constructores, campos y
/// propiedades (ve una inyección aunque el tipo se escriba con otro alias) y el IL de firmas y cuerpos de Application, Web
/// e Infrastructure (ve un <c>GetRequiredService&lt;…&gt;()</c>, un <c>typeof</c> o un cast compilados, se escriban como se
/// escriban). Fuera de <c>src</c>, los tests construyen el concreto a propósito.
/// </para>
///
/// <para>
/// Límites declarados: ninguno ve una resolución por cadena (<c>Type.GetType("…")</c> más reflexión, que también alcanzaría
/// un miembro <c>internal</c>), ni lo que haya fuera de <c>src</c> que referencie Infrastructure (por ejemplo,
/// <c>tools/</c>). Tampoco fijan qué hace el adaptador por dentro: su test de comportamiento
/// (<c>AdaptadorAlmacenOriginalesInboundTests</c>) cubre <c>AbrirOriginalAsync</c>, y un método nuevo en el adaptador solo
/// lo vería la revisión de la PR.
/// </para>
/// </summary>
public class AlmacenDeDiscoSoloPorSusPuertosTests
{
    private const string Registro = "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs";
    private const string Adaptador = "src/CaeManager.Infrastructure/Inbound/FileStorage/AdaptadorAlmacenOriginalesInbound.cs";
    private const string Almacen = "src/CaeManager.Infrastructure/FileStorage/DiskFileStorageService.cs";
    private const string Lectura = "src/CaeManager.Infrastructure/FileStorage/LecturaDeDiscoDelTenantPropietario.cs";

    private static readonly string[] FicherosAutorizados =
    [
        // El propio tipo.
        Almacen,
        // Su registro: el concreto y el puerto del Tenant activo, que resuelve la misma instancia.
        Registro,
        // La lectura entre Tenants, que lo envuelve en otra instancia para llamar a su método internal.
        Lectura,
    ];

    /// <summary>Quién nombra la lectura entre Tenants en <c>src</c>: su definición, quien la implementa, su único consumidor y su registro.</summary>
    private static readonly string[] FicherosQueNombranLaLecturaEntreTenants =
    [
        "src/CaeManager.Application/Common/Archivos/IAlmacenArchivosDelTenantPropietario.cs",
        Lectura,
        Adaptador,
        Registro,
    ];

    /// <summary>Quién nombra el puerto de originales Inbound en <c>src</c>: su definición, sus dos consumidores, el adaptador y el registro.</summary>
    private static readonly string[] FicherosQueNombranElPuertoDeOriginales =
    [
        "src/CaeManager.Application/Inbound/ContratosAcceso/EvidenciasInbound/EvidenciasInboundPuertos.cs",
        "src/CaeManager.Application/Inbound/ContratosAcceso/Queries/ObtenerLecturaInbound/ObtenerLecturaInboundQueries.cs",
        "src/CaeManager.Application/Inbound/ContratosAcceso/EvidenciasInbound/RegistrarEvidenciaInboundCommand.cs",
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
            "quien pida DiskFileStorageService por su tipo concreto llega a la lectura de binarios de otro Tenant sin pasar " +
            "por la lista de consumidores autorizados; se pide por sus puertos");
    }

    [Fact]
    public void Ningun_tipo_inyecta_el_concreto_salvo_su_lectura_entre_Tenants()
    {
        var consumidores = Ensamblados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(DiskFileStorageService)))
            .Where(t => t.DeclaringType != typeof(DiskFileStorageService))
            .Select(t => t.FullName)
            .ToList();
        consumidores.Should().BeEquivalentTo([typeof(LecturaDeDiscoDelTenantPropietario).FullName],
            "el concreto se inyecta por IFileStorageService; solo su lectura entre Tenants lo recibe tal cual");

        NombranEnIl(typeof(DiskFileStorageService)).Should().BeEquivalentTo(
            [typeof(LecturaDeDiscoDelTenantPropietario), typeof(InfrastructureServiceCollectionExtensions)],
            "un GetRequiredService<DiskFileStorageService>() o un cast a él dentro de un método tampoco");
    }

    /// <summary>
    /// La lectura entre Tenants no se alcanza desde <see cref="IFileStorageService"/>: el concreto no implementa el puerto
    /// estrecho ni expone en público nada que no sea de <see cref="IFileStorageService"/>, así que ni un cast ni
    /// <c>dynamic</c> llegan a ella (la composición real lo fija <c>AdaptadorAlmacenOriginalesInboundTests</c>).
    /// </summary>
    [Fact]
    public void El_almacen_del_Tenant_activo_no_expone_la_lectura_entre_Tenants()
    {
        typeof(IAlmacenArchivosDelTenantPropietario).IsAssignableFrom(typeof(DiskFileStorageService)).Should().BeFalse(
            "un consumidor de IFileStorageService abriría la carpeta de otro Tenant con un cast");
        typeof(DiskFileStorageService).GetInterfaces().Should().BeEquivalentTo([typeof(IFileStorageService)]);

        var publicos = typeof(DiskFileStorageService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();
        var delPuerto = typeof(DiskFileStorageService).GetInterfaceMap(typeof(IFileStorageService)).TargetMethods;
        publicos.Should().OnlyContain(m => delPuerto.Contains(m),
            "con dynamic se llega a cualquier miembro público del concreto; solo lo son los de IFileStorageService");
        var interno = typeof(DiskFileStorageService).GetMethod(
            nameof(IAlmacenArchivosDelTenantPropietario.AbrirDelTenantPropietarioAsync), BindingFlags.NonPublic | BindingFlags.Instance);
        interno.Should().NotBeNull("si cambiara de nombre, la comprobación de arriba pasaría sin medir nada");
        interno!.IsAssembly.Should().BeTrue("internal: solo Infrastructure (y sus tests) lo ven");
    }

    /// <summary>
    /// ADR-013 M2 (K-3): la lectura entre Tenants del Núcleo solo la inyecta el adaptador de Inbound, que antes exige que la
    /// referencia sea de la carpeta del Tenant que devolvió la base. Otro consumidor abriría cualquier archivo de cualquier
    /// Tenant con su clave y su Tenant, sin la autoridad de la lectura Inbound.
    /// </summary>
    [Fact]
    public void Solo_el_adaptador_de_Inbound_pide_la_lectura_entre_Tenants()
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

        NombranEnIl(typeof(IAlmacenArchivosDelTenantPropietario)).Should().BeEquivalentTo(
            [
                typeof(AdaptadorAlmacenOriginalesInbound),
                // Quien la implementa y quien la registra.
                typeof(LecturaDeDiscoDelTenantPropietario),
                typeof(InfrastructureServiceCollectionExtensions),
            ],
            "un GetRequiredService<IAlmacenArchivosDelTenantPropietario>() o un cast dentro de un método no lo ve la reflexión");

        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IAlmacenArchivosDelTenantPropietario)))
            .Select(f => f.Key)
            .ToList();
        ficheros.Should().BeEquivalentTo(FicherosQueNombranLaLecturaEntreTenants,
            "el barrido de fuentes ve además un uso en Program.cs o en código que no se haya compilado todavía");
        FuentesDeSrc.Analisis[Registro].Palabras[nameof(IAlmacenArchivosDelTenantPropietario)]
            .Should().Be(1, "la composición la registra una vez y no la pide para nada más");
    }

    /// <summary>
    /// ADR-013 M2 (K-3): el almacén de disco del Núcleo ya no implementa el puerto de Inbound; lo implementa solo el
    /// adaptador, y solo la composición lo registra. El barrido de fuentes no excluye ninguna carpeta: enumera cada fichero
    /// que nombra el puerto, también en Application Inbound.
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

        var ficheros = FuentesDeSrc.Analisis
            .Where(f => f.Value.Palabras.ContainsKey(nameof(IAlmacenOriginalesInbound)))
            .Select(f => f.Key)
            .ToList();
        ficheros.Should().BeEquivalentTo(FicherosQueNombranElPuertoDeOriginales,
            "la definición, los dos handlers que deciden antes la autoridad, el adaptador que lo implementa y su registro; " +
            "un fichero nuevo que lo nombre, también en Application Inbound, pasa por esta lista");
        FuentesDeSrc.Analisis[Registro].Palabras[nameof(IAlmacenOriginalesInbound)].Should().Be(1, "un solo registro");
    }

    [Fact]
    public void Control_los_instrumentos_ven_su_registro_y_una_peticion_dentro_de_un_metodo()
    {
        // Si el registro dejara de nombrar el concreto, la lista se quedaría muerta y el primer test pasaría por vacío.
        FuentesDeSrc.Analisis[Registro].Palabras.Should().ContainKey(nameof(DiskFileStorageService));
        typeof(LecturaDeDiscoDelTenantPropietario).GetInterfaces().Should().Contain(typeof(IAlmacenArchivosDelTenantPropietario));
        // El escaneo por reflexión ve a un consumidor por su constructor primario: sin esto, el de la lectura entre
        // Tenants pasaría igual si el helper dejara de ver constructores.
        typeof(AdaptadorAlmacenOriginalesInbound).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IAlmacenArchivosDelTenantPropietario));
        // Y el de IL ve un typeof dentro de un cuerpo, que la reflexión de firmas no ve.
        var sintetico = typeof(ConsumidorSinteticoDeLaLecturaEntreTenants);
        ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(sintetico.Assembly, typeof(IAlmacenArchivosDelTenantPropietario))
            .Should().Contain(sintetico);
        ReflexionArquitecturaHelper.TiposQueReferencianTipo(sintetico.Assembly, typeof(IAlmacenArchivosDelTenantPropietario))
            .Should().NotContain(sintetico);
    }

    private static List<Type> NombranEnIl(Type tipo) => Ensamblados
        .Select(ReflexionArquitecturaHelper.CargarAssembly)
        .SelectMany(a => ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(a, tipo))
        .Where(t => t != tipo)
        .Distinct()
        .ToList();

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}

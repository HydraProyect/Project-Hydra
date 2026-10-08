using System.Text.RegularExpressions;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P1-M3: los identificadores de entidad nuevos son UUID v7 y salen de una
/// sola fuente, <see cref="IdentificadorEntidad.Nuevo"/>. Tres cosas que este
/// trinquete impide:
///
/// 1. Que un Id de entidad vuelva a generarse con <see cref="Guid.NewGuid"/>
///    (v4) en algún sitio nuevo: todo <c>Guid.NewGuid</c> del código de
///    producción tiene que estar en una de las dos listas de abajo, con su
///    recuento exacto. Un uso nuevo obliga a clasificarlo.
/// 2. Que un secreto o token pase a v7. Un v7 lleva la marca de tiempo en
///    claro y es en parte predecible; por eso <c>Guid.CreateVersion7</c> solo
///    puede aparecer en <see cref="IdentificadorEntidad"/>. Si alguien
///    "moderniza" un sello de seguridad a v7, cae aquí.
/// 3. Que una migración nueva genere un Id en SQL con <c>gen_random_uuid()</c>
///    (v4) en vez de <c>uuidv7()</c> (PostgreSQL 18).
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="NombresDeArchivoFueraDeLosLogsTests"/>: una llamada a un método
/// estático no es una dependencia de tipo, la reflexión no la ve.
/// </summary>
public class IdentificadoresDeEntidadUuidV7Tests
{
    /// <summary>
    /// Tienen que ser impredecibles y NUNCA pueden pasar a v7: sellos de
    /// seguridad, nonces, identificadores de clave y nombres de fichero en
    /// disco o temporales que no deben poder adivinarse.
    /// </summary>
    private static readonly Dictionary<string, int> SecretosYTokens = new()
    {
        // SecurityStamp de Identity: invalida sesiones abiertas; es un secreto.
        ["src/CaeManager.Infrastructure/Identity/ApplicationUser.cs"] = 1,
        ["src/CaeManager.Infrastructure/Identity/SegundoFactorDeCuentasIdentity.cs"] = 1,
        // jti del aserto de cliente de Microsoft 365: nonce anti-repetición.
        ["src/CaeManager.Infrastructure/Integraciones/AsertoClienteMicrosoft365.cs"] = 1,
        // Identificador de la clave HMAC del contexto RLS firmado.
        ["src/CaeManager.Infrastructure/Persistence/ContextoRls/ClaveContextoRls.cs"] = 1,
        // Nombre del blob en disco y de su temporal: no adivinable por diseño.
        ["src/CaeManager.Infrastructure/FileStorage/DiskFileStorageService.cs"] = 2,
        // Directorio temporal de conversión: nombre no predecible en /tmp.
        ["src/CaeManager.Infrastructure/Conversion/LibreOfficeConversorWordPdfService.cs"] = 1,
    };

    /// <summary>
    /// No son Id de entidad: tokens de concurrencia optimista
    /// (<c>Version</c>), identificadores de correlación que no se persisten
    /// como clave, e ids de elementos HTML. Pueden seguir en v4.
    /// </summary>
    private static readonly Dictionary<string, int> NoSonIdDeEntidad = new()
    {
        // ConcurrencyStamp de Identity, renovado al guardar el avatar: token de concurrencia.
        ["src/CaeManager.Infrastructure/Identity/AvatarDeCuentasIdentity.cs"] = 1,
        // Token de concurrencia Version (IVersionable) y quien lo renueva.
        ["src/CaeManager.Domain/AsistenteIa/TareaAsistente.cs"] = 1,
        ["src/CaeManager.Domain/Common/EntidadBase.cs"] = 1,
        ["src/CaeManager.Domain/Integraciones/CredencialIntegracion.cs"] = 1,
        ["src/CaeManager.Domain/Operaciones/AsignacionResponsabilidad.cs"] = 1,
        ["src/CaeManager.Domain/Operaciones/PropuestaApoyoCartera.cs"] = 1,
        ["src/CaeManager.Domain/Operaciones/SolicitudIncorporacionCartera.cs"] = 1,
        ["src/CaeManager.Domain/Plantillas/ItemGeneracionDocumento.cs"] = 1,
        ["src/CaeManager.Domain/Plataforma/ConcesionPrivilegio.cs"] = 1,
        ["src/CaeManager.Domain/Plataforma/EstadoBootstrapPlataforma.cs"] = 2,
        ["src/CaeManager.Domain/Plataforma/OrdenMenuLateral.cs"] = 1,
        ["src/CaeManager.Domain/Plataforma/SesionPrivilegiada.cs"] = 1,
        ["src/CaeManager.Domain/Proyectos/ProyectoTecnico.cs"] = 1,
        ["src/CaeManager.Domain/VigilanciaNormativa/AvisoRevisionNormativa.cs"] = 1,
        ["src/CaeManager.Infrastructure/Persistence/Interceptors/ConcurrenciaOptimistaInterceptor.cs"] = 1,
        // Id del plan de importación en memoria (no se persiste como clave).
        ["src/CaeManager.Infrastructure/Importacion/ClosedXmlImportacionParser.cs"] = 1,
        ["src/CaeManager.Infrastructure/Importacion/ClosedXmlPlantillaClientesService.cs"] = 2,
        ["src/CaeManager.Infrastructure/Importacion/ClosedXmlPlantillaDocumentosService.cs"] = 2,
        // Referencia de correlación de un error en el log.
        ["src/CaeManager.Web/Components/Layout/MainLayout.razor.cs"] = 1,
        // Claves locales de UI: toasts y filas de la subida masiva.
        ["src/CaeManager.Web/Components/DesignSystem/ToastService.cs"] = 1,
        ["src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor.cs"] = 1,
        // Atributos id de HTML únicos por instancia de componente.
        ["src/CaeManager.Web/Components/DesignSystem/CampoBuscarSelect.razor"] = 2,
        ["src/CaeManager.Web/Components/DesignSystem/CampoSelect.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/CampoSelectAvanzado.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/CampoTextarea.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/CampoTexto.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/Drawer.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/MenuAcciones.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/Modal.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/Pestanas.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/SeccionColapsable.razor"] = 1,
        ["src/CaeManager.Web/Components/DesignSystem/SelectorEntidad.razor"] = 2,
        ["src/CaeManager.Web/Components/DesignSystem/ZonaSoltarArchivo.razor"] = 1,
        ["src/CaeManager.Web/Features/Bandeja/Components/GrupoCola.razor"] = 1,
        ["src/CaeManager.Web/Features/Bandeja/Components/SelectorLoteDocumental.razor"] = 1,
        ["src/CaeManager.Web/Features/Documentos/Components/FirmaEnCampoTab.razor.cs"] = 1,
        ["src/CaeManager.Web/Features/Plantillas/Pages/ConfigurarPlantilla.razor.cs"] = 1,
        ["src/CaeManager.Web/Features/Usuarios/Pages/MiFirma.razor.cs"] = 1,
    };

    private const string FuenteUnicaV7 = "src/CaeManager.Domain/Common/IdentificadorEntidad.cs";

    /// <summary>
    /// <c>gen_random_uuid()</c> ya presente en migraciones. Los de la línea
    /// base son SecurityStamp y ConcurrencyStamp (2, secretos), el DEFAULT de
    /// una columna Version (1) y los Id de los dos INSERT de auditoría de
    /// <c>app_restablecer_segundo_factor_por_soporte</c> (2): hueco declarado,
    /// pasarán a <c>uuidv7()</c> cuando esa función se redefina por otro
    /// motivo. Una migración nueva no entra en la lista.
    /// </summary>
    private static readonly Dictionary<string, int> GenRandomUuidEnMigraciones = new()
    {
        ["src/CaeManager.Migrations.PostgreSQL/Migrations/20260926160042_LineaBaseCompactada.Esquema.cs"] = 5,
    };

    private static readonly Regex PatronNewGuid = new(@"\bGuid\s*\.\s*NewGuid\b", RegexOptions.Compiled);
    private static readonly Regex PatronCreateVersion7 = new(@"\bCreateVersion7\b", RegexOptions.Compiled);
    private static readonly Regex PatronGenRandomUuid = new(@"\bgen_random_uuid\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Todo_Guid_NewGuid_de_produccion_esta_clasificado_como_secreto_o_como_no_Id_de_entidad()
    {
        var raiz = RaizDelRepositorio();
        var clasificados = SecretosYTokens.Concat(NoSonIdDeEntidad).ToDictionary(p => p.Key, p => p.Value);
        var medidos = ContarPorFichero(raiz, "src", ["*.cs", "*.razor"], PatronNewGuid);

        var divergencias = clasificados.Keys.Union(medidos.Keys)
            .Select(ruta => (Ruta: ruta, Esperado: clasificados.GetValueOrDefault(ruta), Medido: medidos.GetValueOrDefault(ruta)))
            .Where(x => x.Esperado != x.Medido)
            .OrderBy(x => x.Ruta, StringComparer.Ordinal)
            .Select(x => $"{x.Ruta}: esperado {x.Esperado}, medido {x.Medido}")
            .ToList();

        string.Join(Environment.NewLine, divergencias).Should().BeEmpty(
            "un Id de entidad se genera con IdentificadorEntidad.Nuevo() (UUID v7); Guid.NewGuid() solo vale para " +
            "secretos y tokens (SecretosYTokens) o para lo que no es Id de entidad (NoSonIdDeEntidad). Si el uso " +
            "nuevo es uno de esos dos casos, añádelo a su lista con su recuento; si es un Id de entidad, usa la " +
            "fuente única. Si un uso desapareció, baja el recuento");
    }

    [Fact]
    public void Guid_CreateVersion7_solo_se_llama_desde_la_fuente_unica()
    {
        var raiz = RaizDelRepositorio();
        var medidos = ContarPorFichero(raiz, "src", ["*.cs", "*.razor"], PatronCreateVersion7);

        medidos.Should().Equal(new Dictionary<string, int> { [FuenteUnicaV7] = 1 },
            "un UUID v7 deja ver su marca de tiempo y es en parte predecible: no puede usarse para sellos, " +
            "nonces ni enlaces no adivinables, y los Id de entidad tienen una sola fuente (IdentificadorEntidad)");
    }

    [Fact]
    public void Ninguna_migracion_nueva_genera_uuid_v4_en_SQL()
    {
        var raiz = RaizDelRepositorio();
        var medidos = ContarPorFichero(raiz, "src/CaeManager.Migrations.PostgreSQL", ["*.cs"], PatronGenRandomUuid);

        medidos.Should().Equal(GenRandomUuidEnMigraciones,
            "un Id generado en SQL usa uuidv7() (PostgreSQL 18), no gen_random_uuid(); si es un secreto o un " +
            "token de concurrencia, añade el fichero a GenRandomUuidEnMigraciones con su motivo");
    }

    [Fact]
    public void Los_patrones_reconocen_la_forma_que_vigilan_e_ignoran_comentarios()
    {
        EsCodigoQueCasa("    public Guid Id { get; protected set; } = Guid.NewGuid();", PatronNewGuid)
            .Should().BeTrue("es la forma exacta que P1-M3 retiró de Entity");
        EsCodigoQueCasa("        var ids = filas.Select(_ => System.Guid.NewGuid()).ToList();", PatronNewGuid)
            .Should().BeTrue("el nombre cualificado también genera un v4");
        EsCodigoQueCasa("        var generar = (Func<Guid>)Guid.NewGuid;", PatronNewGuid)
            .Should().BeTrue("el grupo de métodos también genera un v4");
        EsCodigoQueCasa("    // HasData exige valores fijos (no Guid.NewGuid())", PatronNewGuid)
            .Should().BeFalse("documentar el patrón no es usarlo");
        EsCodigoQueCasa("    /// Guid.NewGuid()), así que no puede colisionar", PatronNewGuid)
            .Should().BeFalse("un comentario XML tampoco");
        EsCodigoQueCasa("    public static Guid Nuevo() => Guid.CreateVersion7();", PatronCreateVersion7)
            .Should().BeTrue();
        EsCodigoQueCasa("    (gen_random_uuid(), v_tenant, 'Usuario', p_usuario, 'Modificado',", PatronGenRandomUuid)
            .Should().BeTrue();
        EsCodigoQueCasa("    (uuidv7(), v_tenant, 'Usuario', p_usuario, 'Modificado',", PatronGenRandomUuid)
            .Should().BeFalse("uuidv7() es la forma correcta");
    }

    [Fact]
    public void La_fuente_unica_genera_UUID_v7_con_la_hora_actual()
    {
        var antes = DateTimeOffset.UtcNow;
        var id = IdentificadorEntidad.Nuevo();
        var despues = DateTimeOffset.UtcNow;

        id.Version.Should().Be(7);
        (id.ToByteArray(bigEndian: true)[8] & 0xC0).Should().Be(0x80,
            "la variante RFC 9562 son los dos bits altos del octeto 8 a 10");
        var marca = MarcaDeTiempoV7(id);
        marca.Should().BeOnOrAfter(antes.AddMilliseconds(-1)).And.BeOnOrBefore(despues.AddMilliseconds(1));
    }

    [Fact]
    public void Una_entidad_nueva_recibe_su_Id_de_la_fuente_unica()
    {
        new EntidadDePrueba().Id.Version.Should().Be(7,
            "Entity.Id es la única fuente de Id de entidad de dominio");
    }

    /// <summary>
    /// ApplicationUser e IdentityRole&lt;Guid&gt; no heredan de <see cref="Entity"/>:
    /// su Id lo asigna el generador de valores del proveedor EF al añadirlos
    /// (Identity no lo rellena). Npgsql EF genera v7 para claves Guid; si
    /// cambiara de proveedor o de versión y volviera a v4, cae aquí.
    /// </summary>
    [Fact]
    public void Las_cuentas_y_roles_de_Identity_reciben_Id_v7_del_proveedor_EF()
    {
        using var contexto = CrearContextoSinConexion();

        var usuario = new ApplicationUser { UserName = "uuid-v7@ejemplo.test" };
        var rol = new IdentityRole<Guid>("RolDePrueba");
        contexto.Add(usuario);
        contexto.Add(rol);

        usuario.Id.Version.Should().Be(7, "el Id de la cuenta lo genera Npgsql al añadirla");
        rol.Id.Version.Should().Be(7, "el Id del rol lo genera Npgsql al añadirlo");
    }

    private static DateTimeOffset MarcaDeTiempoV7(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        long milisegundos = 0;
        for (var i = 0; i < 6; i++)
            milisegundos = (milisegundos << 8) | bytes[i];
        return DateTimeOffset.FromUnixTimeMilliseconds(milisegundos);
    }

    private static bool EsCodigoQueCasa(string linea, Regex patron)
    {
        var contenido = linea.TrimStart();
        if (contenido.StartsWith("//", StringComparison.Ordinal)
            || contenido.StartsWith("*", StringComparison.Ordinal)
            || contenido.StartsWith("/*", StringComparison.Ordinal)
            || contenido.StartsWith("@*", StringComparison.Ordinal))
        {
            return false;
        }

        return patron.IsMatch(linea);
    }

    private static Dictionary<string, int> ContarPorFichero(string raiz, string carpeta, string[] extensiones, Regex patron)
    {
        var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(directorio).Should().BeTrue($"si {carpeta} cambia de sitio, este ratchet deja de vigilar nada");

        var ficheros = extensiones
            .SelectMany(ext => Directory.EnumerateFiles(directorio, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        ficheros.Should().NotBeEmpty($"{carpeta} tiene que contener código que vigilar");

        return ficheros
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace(Path.DirectorySeparatorChar, '/'),
                          Cuenta: File.ReadLines(f).Where(l => EsCodigoQueCasa(l, patron)).Sum(l => patron.Matches(l).Count)))
            .Where(x => x.Cuenta > 0)
            .ToDictionary(x => x.Ruta, x => x.Cuenta);
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }

    private sealed class EntidadDePrueba : Entity;
}

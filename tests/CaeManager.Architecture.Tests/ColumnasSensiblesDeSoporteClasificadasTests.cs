using System.Text.RegularExpressions;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Cada columna con nombre sensible del modelo tiene una decisión explícita sobre Soporte TALVEG</b>
/// (OWASP ASVS 4.0.3 V4.1.3, mínimo privilegio).
///
/// <para>
/// La línea base concede <c>SELECT</c> a <c>cae_app_soporte</c> en las tablas nuevas por defecto, así que una
/// columna recién añadida con un nombre como <c>…Hash</c>, <c>…Json</c>, <c>…Token</c> o <c>Codigo…</c> queda
/// legible por Soporte TALVEG sin que nadie lo haya decidido. Este test lee el modelo real de EF y exige que
/// cada una de esas columnas figure en <see cref="Clasificaciones"/> con uno de tres estados:
/// <see cref="Decision.Retirada"/> (Soporte TALVEG no tiene <c>SELECT</c>),
/// <see cref="Decision.ConcedidaJustificada"/> (lo tiene y la razón explica por qué es necesario) o
/// <see cref="Decision.ConcedidaPorRevisar"/> (lo tiene por la línea base y falta decidir).
/// </para>
///
/// <para>
/// La etiqueta es declarativa: lo que hay de verdad en la base migrada lo miden
/// <c>SoporteSinColumnasSecretasTests</c> (texto de las migraciones) y las pruebas de integración de Soporte
/// TALVEG. Al retirar una columna en una migración, se pasa su entrada a <c>Retirada</c> en el mismo cambio.
/// </para>
/// </summary>
public class ColumnasSensiblesDeSoporteClasificadasTests
{
    private enum Decision
    {
        /// <summary>Soporte TALVEG no tiene <c>SELECT</c> sobre la columna.</summary>
        Retirada,

        /// <summary>Soporte TALVEG la lee y la razón justifica que la necesita.</summary>
        ConcedidaJustificada,

        /// <summary>Soporte TALVEG la lee por la línea base; falta contrastar si la necesita.</summary>
        ConcedidaPorRevisar,
    }

    private sealed record Clasificacion(Decision Decision, string Razon);

    // La clasificación congela el SELECT vigente; que exista una concesión no demuestra que sea necesaria.
    // Las entradas ConcedidaPorRevisar son decisiones que todavía deben contrastarse, no aprobaciones.
    private static readonly Dictionary<string, Clasificacion> Clasificaciones = new(StringComparer.Ordinal)
    {
        ["AccesosPlataformaCaeEmpresa.Contrasena"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["AccesosPlataformaCaeEmpresa.ContrasenaCambiadaEnUtc"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["AccesosPlataformaCaeEmpresa.ContrasenaCambiadaPorUsuarioId"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["AspNetUsers.DebeCambiarContrasena"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["AspNetUsers.PasswordHash"] = new(Decision.Retirada, "Retirada por la migración 20261101110000_SoporteSinPasswordHashNiSecurityStamp."),
        ["AuditoriasExtraccionIa.HashSha256"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["AuditoriasExtraccionIa.ProveedorCodigo"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["AvisosRevisionNormativa.UrlHtml"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["CanalesGestionDocumental.Contrasena"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["Centros.CodigoCentro"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["CierresMensualesTenantBeneficiario.FotoJson"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["ClavesApi.HashClave"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesAccesoEmpresa.Contrasena"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesAccesoSubcontrata.Contrasena"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesConexionPlataformaCae.SecretoApi"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesIntegracion.AccessToken"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesIntegracion.AccessTokenExpiraUtc"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["CredencialesIntegracion.RefreshToken"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["DocumentosGenerados.CodigoSeguroVerificacion"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["DocumentosGenerados.DatosUtilizadosJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["DocumentosGenerados.HashSha256Impreso"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["EventosWebhook.PayloadCrudo"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["EventosWebhook.PayloadRedactado"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["ExtraccionesIaCache.ExtraccionJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["ExtraccionesIaCache.HashSha256"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["FiltrosGuardados.ValoresJson"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["FirmasEnCampoDocumento.HashSha256Pdf"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["IncidentesSeguridadSospechados.SujetoClave"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["LineasWhatsApp.TokenAcceso"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["LotesGeneracionDocumento.ContextoJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["MacrosRespuesta.CuerpoHtml"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["Mensajes.CuerpoHtml"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["PasosTareaAsistente.AvisosJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["PasosTareaAsistente.CamposPendientesJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["PasosTareaAsistente.DatosJson"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["PlantillasDocumento.CodigoFormato"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["PlantillasDocumentoVersion.HashSha256ArchivoOriginal"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; necesidad por revisar."),
        ["PlataformasCaeClienteEmpresarial.CodigoCliente"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["ProveedoresPlataformaCae.Codigo"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["AspNetUserLogins.ProviderKey"] = new(Decision.Retirada, "Retirada por la migración 20261101060000_SoporteSinLecturaDeUserLogins."),
        ["AspNetUsers.SecurityStamp"] = new(Decision.Retirada, "Retirada por la migración 20261101110000_SoporteSinPasswordHashNiSecurityStamp."),
        ["SesionesUsuario.Sid"] = new(Decision.Retirada, "Retirada por la migración 20261031000000_SoporteSinSidSesionUsuario."),
        ["SuscripcionesWebhook.ClientState"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
        ["Tenants.LogoArchivoClave"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; dato operativo o metadato, necesidad por revisar."),
        ["UsosConexionPlataformaCae.ClaveIdempotencia"] = new(Decision.ConcedidaPorRevisar, "SELECT vigente; Guid generado por el servidor para correlacionar el uso, no es secreto."),
        ["VerificacionesDocumentoOficial.CodigoVerificacion"] = new(Decision.Retirada, "Sin SELECT para Soporte TALVEG tras las migraciones vigentes."),
    };

    // Los términos se buscan como componentes de nombres PascalCase, sin confundir, por ejemplo,
    // «Token» con «Tokenizacion» ni «Sid» con «Presidente».
    private static readonly Regex NombreSensible = new(
        @"(?:JSON|Json|Token|Secret|Secreto|Hash|HTML|Html|Cuerpo|SID|Sid|Codigo|Clave|Password|Contrasena|Payload|ClientState|Credential|Credencial|SecurityStamp|ProviderKey)(?=$|[A-Z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Theory]
    [InlineData("AccessToken", true)]
    [InlineData("DatosJson", true)]
    [InlineData("DatosJSON", true)]
    [InlineData("CodigoSeguroVerificacion", true)]
    [InlineData("PasswordHash", true)]
    [InlineData("Sid", true)]
    [InlineData("ClientState", true)]
    [InlineData("SecurityStamp", true)]
    [InlineData("ProviderKey", true)]
    [InlineData("ConcurrencyStamp", false)]
    [InlineData("Presidente", false)]
    [InlineData("Tokenizacion", false)]
    [InlineData("Nombre", false)]
    public void El_detector_identifica_componentes_del_nombre(string nombre, bool esperado)
    {
        NombreSensible.IsMatch(nombre).Should().Be(esperado);
    }

    [Fact]
    public void Toda_columna_con_nombre_sensible_tiene_una_decision_explicita()
    {
        using var contexto = CrearContextoSinConexion();
        var modelo = contexto.GetService<IDesignTimeModel>().Model;
        modelo.GetEntityTypes().Count().Should().BeGreaterThan(50);
        modelo.GetEntityTypes().SelectMany(e => e.GetProperties()).Count().Should().BeGreaterThan(500);

        var medidas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entidad in modelo.GetEntityTypes())
        {
            var tabla = entidad.GetTableName();
            if (tabla is null)
                continue;

            var objeto = StoreObjectIdentifier.Table(tabla, entidad.GetSchema());
            foreach (var propiedad in entidad.GetProperties())
            {
                var columna = propiedad.GetColumnName(objeto);
                if (columna is not null && (NombreSensible.IsMatch(propiedad.Name) || NombreSensible.IsMatch(columna)))
                {
                    // Una tabla fuera del esquema public también cuenta, con el esquema por delante.
                    var nombreTabla = entidad.GetSchema() is { } esquema && esquema != "public" ? $"{esquema}.{tabla}" : tabla;
                    medidas.Add($"{nombreTabla}.{columna}");
                }
            }
        }

        medidas.Should().Contain("AspNetUsers.PasswordHash", "el detector debe observar una columna conocida del modelo");
        var sinClasificar = medidas.Except(Clasificaciones.Keys).OrderBy(x => x).ToArray();
        sinClasificar.Should().BeEmpty("sin clasificar: " + string.Join(", ", sinClasificar) + ". La línea base concede SELECT a Soporte TALVEG (cae_app_soporte) en las tablas nuevas; " +
            "para cada tabla.columna nueva con nombre sensible hay que decidir: o bien retirarla en la migración que la crea " +
            "(REVOKE y GRANT SELECT por columnas sin ella) y añadirla aquí como Retirada, o bien añadirla como " +
            "ConcedidaJustificada con la razón por la que Soporte TALVEG la necesita (o ConcedidaPorRevisar si aún no se sabe)");
        var obsoletas = Clasificaciones.Keys.Except(medidas).OrderBy(x => x).ToArray();
        obsoletas.Should().BeEmpty("obsoletas: " + string.Join(", ", obsoletas) + ". La lista debe reflejar las columnas actuales del modelo: si una columna se renombró " +
            "o se eliminó, quita su entrada; si una migración retira una columna que figuraba como concedida, " +
            "cambia su entrada a Retirada en el mismo cambio y amplía SoporteSinColumnasSecretasTests");
        Clasificaciones.Values.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Razon));
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

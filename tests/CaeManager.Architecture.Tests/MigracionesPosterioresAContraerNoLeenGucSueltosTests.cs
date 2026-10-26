using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// P6, fase «contraer»: la migración <c>ContraeContextoRlsFirmado</c> pasó todas
/// las políticas RLS y las funciones de <c>public</c> al contexto firmado
/// (<c>app_ctx_*()</c>). Una migración posterior que vuelva a escribir
/// <c>current_setting('app.tenant_id' …)</c> reabriría el hueco que P6 cierra:
/// ese GUC lo fija cualquiera que ejecute SQL en la sesión. El ratchet de catálogo
/// (integración) lo vería al migrar; este lo ve en el texto, antes de compilar la
/// base, incluidas las comillas dobladas de los literales de C#. Se decide por una
/// lista congelada de nombres, no por la marca de tiempo, que cualquiera elige.
/// </summary>
public class MigracionesPosterioresAContraerNoLeenGucSueltosTests
{
    private const string MigracionContraer = "20261025120000_ContraeContextoRlsFirmado";

    /// <summary>
    /// Migraciones anteriores a «contraer» que leen los GUC sueltos, congeladas: su
    /// SQL ya se aplicó y «contraer» lo reescribe en el catálogo. La lista solo puede
    /// encoger; una migración que no esté aquí y lea el GUC da rojo, se llame como
    /// se llame (una marca de tiempo anterior a «contraer» no la exime).
    /// </summary>
    private static readonly HashSet<string> AnterioresCongeladas = new(StringComparer.Ordinal)
    {
        "20260926160042_LineaBaseCompactada.Esquema",
        "20261003165050_ToleranciaDeAccesoPorCentroYClienteEmpresarial",
        "20261008221604_AnadePropuestasApoyoCartera",
        "20261008221829_AnadeTenantALosFiltrosGuardados",
        "20261008224121_AnadeEncargosAdministracion",
        "20261010153439_AnadeOrdenCajasFicha",
        "20261015171632_AnadeFichaVehiculo",
        "20261015180535_AnadeEncargoAdministracionPleno",
        "20261015193636_EnmascaraAuditoriaDeTrabajadorAnonimizado",
        "20261015202028_AnadeProyectoTipoEvento",
        "20261017175534_AnadeEstadosEsperaConversacion",
        "20261017221324_CierreMensualTenantBeneficiario",
        "20261018003253_AnadeIntencionesConversacion",
        "20261018030000_AnadePropuestasRecordatorioConversacion",
        "20261018040000_AnadeAlertasCriticasConversacion",
        "20261018155155_AnadeNotaInternaATrabajadorYCentro",
        "20261018182902_AnadeContactoEmergenciaATrabajador",
        "20261018193814_AnadePlataformaCaeDeClienteEmpresarial",
        "20261018210526_AnadeDelegacionYOficioEmpresa",
        "20261020182202_AnadeTarifasOperadorCae",
        "20261022153923_AnadeCambiosEstadoAcreditacionPlataforma",
        "20261023130000_AnadeContratosAcceso",
        "20261023140000_AnadeAutorizacionesLecturaDocumental",
        "20261023150000_AnadeLecturaInboundEntreTenants",
        "20261023195346_AnadeEvidenciasInbound",
        "20261023221557_AnadeRegistroIniciosSesion",
        "20261024033056_AnadeEstadoMaterializado",
        "20261024235029_AnadeEnviosReclamacion",
    };

    private static readonly Regex LecturaDeGucSuelto = new(
        @"current_setting\(\s*'{1,2}app\.(tenant_id|tenant_origen_id|usuario_id)'",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Ninguna_migracion_posterior_a_contraer_lee_los_GUC_sueltos()
    {
        var migraciones = Directory
            .GetFiles(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Migrations.PostgreSQL", "Migrations"), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(f => (Nombre: Path.GetFileNameWithoutExtension(f), Texto: File.ReadAllText(f)))
            .ToList();

        migraciones.Should().Contain(m => m.Nombre == MigracionContraer,
            "control del instrumento: la migración de «contraer» tiene que estar en el directorio que se mira");
        var leenGuc = migraciones.Where(m => LecturaDeGucSuelto.IsMatch(m.Texto)).Select(m => m.Nombre).ToHashSet(StringComparer.Ordinal);

        leenGuc.Should().Contain("20261015193636_EnmascaraAuditoriaDeTrabajadorAnonimizado",
            "control del instrumento: esa migración sí leía los GUC sueltos, así que el patrón tiene que encontrarla");

        leenGuc.Except(AnterioresCongeladas).Order(StringComparer.Ordinal).Should().BeEmpty(
            "desde «contraer» las políticas y funciones leen (SELECT app_ctx_tenant_id()), " +
            "(SELECT app_ctx_tenant_origen_id()) o (SELECT app_ctx_usuario_id()); el GUC suelto lo fija " +
            "cualquiera con la credencial de tráfico");

        AnterioresCongeladas.Except(leenGuc).Order(StringComparer.Ordinal).Should().BeEmpty(
            "la lista congelada solo encoge: si una migración ya no lee el GUC (o se borró), sácala de la lista");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}

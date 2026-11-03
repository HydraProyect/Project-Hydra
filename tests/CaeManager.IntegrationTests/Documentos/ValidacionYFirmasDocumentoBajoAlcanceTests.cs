using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// SEG-16 (OWASP ASVS 4.0.3 V4.2.1), con el <see cref="AlcanceDatosService"/> real y el rol de runtime: dentro del
/// mismo Tenant, donde RLS no frena nada, un Gestor CAE sin Asignación de Cartera no lee la validación oficial, el
/// <c>FirmanteNif</c> ni las firmas en campo de un Documento cuyo Id conoce, y uno con cartera sí. Sin filas sembradas
/// visibles para el segundo, el negativo del primero no demostraría nada.
/// </summary>
public class ValidacionYFirmasDocumentoBajoAlcanceTests : IAsyncLifetime
{
    private const string Nif = "12345678Z";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioConCartera = Guid.NewGuid();
    private TenantActualAmbiental _tenantActual = null!;
    private Guid _documentoId;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        var tenant = new Tenant("Tenant de prueba del alcance");
        _tenantActual = new TenantActualAmbiental { TenantId = tenant.Id };

        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantActual);
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();

        var cliente = Empresa.CrearComoCliente("Cliente de prueba S.A.", "B10380186", false, null, null);
        var empresa = new Empresa("Empresa de prueba S.L.", "B10380194");
        var tipo = new TipoDocumento("Corriente de prueba", 12, true, 1, AmbitoAplicacion.Empresa);
        contexto.Empresas.AddRange(cliente, empresa);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Interna(tenant.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora, null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _usuarioConCartera, AmbitoAsignacion.Universal, ahora, null, ahora));

        var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.NoCaduca);
        contexto.Documentos.Add(documento);
        contexto.VerificacionesDocumentoOficial.Add(new VerificacionDocumentoOficial(
            documento.Id, PerfilDocumentoOficial.CorrienteTgss, NivelConfianzaDocumental.FirmaValidaSinRevocacion,
            "CSV-123", null, null, null, null, ResultadoCotejoDocumentoOficial.Coincide,
            DecisionValidacionOficial.AutoValidado, ""));
        contexto.FirmasDigitalesDocumento.Add(new FirmaDigitalDocumento(
            documento.Id, 0, EstadoFirmaPdf.Valida, cadenaConfiable: true, ComprobacionRevocacion.EmbebidaLtv,
            "Emisor de prueba", "SN-1", esSelloDeOrgano: false, "Firmante de prueba", Nif, ahora,
            tieneSelloDeTiempo: true, cubreDocumentoCompleto: true, motivoInvalidez: null));
        contexto.FirmasEnCampoDocumento.Add(new FirmaEnCampoDocumento(
            documento.Id, Guid.NewGuid(), "Firmante de prueba", Roles.GestorCae, ahora, null,
            new string('a', FirmaEnCampoDocumento.LongitudHashSha256)));
        await contexto.SaveChangesAsync();
        _documentoId = documento.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_lee_la_validacion_ni_las_firmas_del_Documento()
    {
        var (validacion, firmas) = await LeerComoGestorAsync(Guid.NewGuid());

        validacion.Should().BeNull();
        firmas.Should().BeEmpty();
    }

    [Fact]
    public async Task Gestor_CAE_con_cartera_lee_la_validacion_con_su_NIF_y_las_firmas()
    {
        var (validacion, firmas) = await LeerComoGestorAsync(_usuarioConCartera);

        validacion.Should().NotBeNull("control del instrumento: el negativo solo vale si estas filas son legibles");
        validacion!.Firmas.Should().ContainSingle().Which.FirmanteNif.Should().Be(Nif);
        firmas.Should().ContainSingle();
    }

    private async Task<(ValidacionOficialDocumentoDto? Validacion, IReadOnlyList<FirmaEnCampoDocumentoDto> Firmas)> LeerComoGestorAsync(Guid usuarioId)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId, Roles.GestorCae, tenantOrigenId: _tenantActual.TenantId);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantActual),
                new TenantRlsConnectionInterceptor(_tenantActual, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        await using var runtime = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantActual);
        var alcance = new AlcanceDatosService(runtime, usuario, _tenantActual, new SesionPrivilegiadaAusente());

        var validacion = await new ObtenerValidacionOficialDocumentoQueryHandler(runtime, runtime, alcance, new SinClienteActivo())
            .Handle(new(_documentoId), CancellationToken.None);
        var firmas = await new ObtenerFirmasEnCampoDocumentoQueryHandler(runtime, runtime, alcance)
            .Handle(new(_documentoId), CancellationToken.None);
        return (validacion, firmas);
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}

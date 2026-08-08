using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Reclamaciones;

/// <summary>
/// Cubre el join central (Documento de Trabajador → Asignación activa →
/// Centro → Cliente) que agrupa por Cliente los documentos reclamables en la
/// ventana de 3 meses, más el ciclo completo de envío contra Postgres real —
/// mismo patrón que ObtenerAlertasQueryFaltantesTests.
/// </summary>
public class ReclamacionDocumentalTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _trabajadorId;
    private Guid _tipoDocumentoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = new Cliente("Reclamación Test S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Contratista de Prueba S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro de prueba");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Reclamación", "Documento Prueba", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipo = new TipoDocumento("Certificado médico", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _trabajadorId = trabajador.Id;
        _tipoDocumentoId = tipo.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_documento_que_vence_en_2_meses_aparece_agrupado_bajo_su_cliente()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura, new ContactosClienteServiceFalso(["cliente@example.com"]));

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);

        var lote = lotes.Should().ContainSingle(l => l.ClienteId == _clienteId).Subject;
        lote.Documentos.Should().ContainSingle();
        lote.DestinatariosEmail.Should().ContainSingle("cliente@example.com");
        lote.UltimaReclamacionFechaUtc.Should().BeNull();
    }

    [Fact]
    public async Task Un_documento_vigente_que_vence_dentro_de_6_meses_no_aparece()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura, new ContactosClienteServiceFalso(["cliente@example.com"]));

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);

        lotes.Should().NotContain(l => l.ClienteId == _clienteId);
    }

    [Fact]
    public async Task Enviar_la_reclamacion_persiste_el_lote_y_envia_el_email()
    {
        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
        }

        var emailServiceFalso = new EmailServiceFalso();
        await using (var contexto = CrearContexto())
        {
            var handler = CrearCommandHandler(
                contexto, new ContactosClienteServiceFalso(["cliente@example.com"]), emailServiceFalso);

            var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        emailServiceFalso.Enviados.Should().ContainSingle(e => e.Destinatario == "cliente@example.com");

        await using var lectura = CrearContexto();
        (await lectura.ReclamacionesDocumentales.CountAsync()).Should().Be(1);

        var handlerConsulta = CrearQueryHandler(lectura, new ContactosClienteServiceFalso(["cliente@example.com"]));
        var lotes = await handlerConsulta.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);
        lotes.Should().ContainSingle(l => l.ClienteId == _clienteId).Which.UltimaReclamacionFechaUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Sin_usuario_de_portal_con_email_falla_al_enviar()
    {
        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
        }

        await using var contexto2 = CrearContexto();
        var handler = CrearCommandHandler(contexto2, new ContactosClienteServiceFalso([]), new EmailServiceFalso());

        var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDestinatario");
    }

    private static ObtenerLoteReclamacionQueryHandler CrearQueryHandler(CaeManagerDbContext contexto, IContactosClienteService contactos) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contactos);

    private static EnviarReclamacionCommandHandler CrearCommandHandler(
        CaeManagerDbContext contexto, IContactosClienteService contactos, IEmailService emailService) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), contactos, emailService,
            new ReclamacionDocumentalRepository(contexto), new CurrentUserServiceFalso(Guid.NewGuid()), contexto);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private class ContactosClienteServiceFalso(IReadOnlyList<string> emails) : IContactosClienteService
    {
        public Task<IReadOnlyList<string>> ObtenerEmailsPortalAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(emails);
    }

    private class EmailServiceFalso : IEmailService
    {
        public List<(string Destinatario, string Asunto)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
        {
            Enviados.Add((destinatarioEmail, asunto));
            return Task.FromResult(Result.Exito());
        }
    }
}

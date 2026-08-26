using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Contactos;

/// <summary>
/// Agenda de contactos por entidad (requisito del usuario 2026-08-13): en un
/// mismo Cliente el RLC se le pide a Finanzas, los aptos médicos a otra
/// persona y los EPIs a otra. Lo que se cubre aquí es que cada contacto
/// retiene sus tipos de documento y que el propietario polimórfico no se puede
/// romper ni desde el dominio ni desde la base de datos.
/// </summary>
public class AgendaContactosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _tipoRlcId;
    private Guid _tipoAptoMedicoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Agenda Test S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var rlc = new TipoDocumento("RLC/TC1", 1, true, 1, AmbitoAplicacion.Empresa);
        var aptoMedico = new TipoDocumento("Apto médico laboral", 12, true, 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.AddRange(rlc, aptoMedico);

        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoRlcId = rlc.Id;
        _tipoAptoMedicoId = aptoMedico.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_contactos_del_mismo_cliente_retienen_cada_uno_sus_tipos_de_documento()
    {
        await GuardarAsync("Finanzas", "finanzas@cliente.test", [_tipoRlcId]);
        await GuardarAsync("Vigilancia de la Salud", "salud@cliente.test", [_tipoAptoMedicoId]);

        var agenda = await LeerAgendaAsync();

        agenda.Should().HaveCount(2);
        agenda.Single(c => c.Nombre == "Finanzas").TipoDocumentoNombres.Should().ContainSingle().Which.Should().Be("RLC/TC1");
        agenda.Single(c => c.Nombre == "Vigilancia de la Salud").TipoDocumentoNombres.Should().ContainSingle().Which.Should().Be("Apto médico laboral");
    }

    [Fact]
    public async Task Editar_un_contacto_reemplaza_sus_tipos_en_vez_de_acumularlos()
    {
        var contactoId = await GuardarAsync("Responsable PRL", "prl@cliente.test", [_tipoRlcId, _tipoAptoMedicoId]);

        // Mismo contacto, ahora solo con uno de los dos tipos.
        await GuardarAsync("Responsable PRL", "prl@cliente.test", [_tipoAptoMedicoId], contactoId);

        var contacto = (await LeerAgendaAsync()).Should().ContainSingle().Which;
        contacto.TipoDocumentoIds.Should().ContainSingle().Which.Should().Be(_tipoAptoMedicoId);
    }

    [Fact]
    public async Task El_telefono_se_normaliza_a_E164_quitando_separadores()
    {
        await GuardarAsync("Con teléfono", "tel@cliente.test", [], telefono: "00 34 600-111 222");

        var contacto = (await LeerAgendaAsync()).Should().ContainSingle().Which;
        contacto.Telefono.Should().Be("+34600111222");
    }

    [Fact]
    public async Task La_base_de_datos_rechaza_un_contacto_con_dos_propietarios()
    {
        // El CHECK XOR es el backstop: el dominio ya lo impide con factorías
        // (DeCliente/DeEmpresa/…), pero un INSERT que se saltara el dominio
        // dejaría una agenda que pertenece a dos sitios a la vez.
        await using var contexto = CrearContexto();

        var empresa = new Empresa("Empresa Agenda S.L.", "B87654323");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var accion = async () => await contexto.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ContactosAgenda"
                ("Id","ClienteId","EmpresaId","SubcontrataId","CentroId","Nombre","Email",
                 "EsPredeterminado","RecibeProgramacionVisitas","RecibeFacturacion",
                 "TenantId","Version","CreadoEnUtc","EstaEliminado")
            VALUES ({0},{1},{2},NULL,NULL,'Dos dueños','x@y.test',false,false,false,{3},{4},now(),false);
            """,
            Guid.NewGuid(), _clienteId, empresa.Id, _tenant, Guid.NewGuid());

        await accion.Should().ThrowAsync<Exception>("un contacto no puede pertenecer a un Cliente y a una Empresa a la vez");
    }

    [Fact]
    public async Task La_agenda_de_un_centro_no_se_mezcla_con_la_de_su_cliente()
    {
        Guid centroId;
        await using (var contexto = CrearContexto())
        {
            var empresa = new Empresa("Empresa del Centro S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(_clienteId, empresa.Id, "Centro con agenda propia");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            centroId = centro.Id;

            contexto.ContactosAgenda.Add(ContactoAgenda.DeCentro(centroId, "Jefe de obra", "obra@centro.test"));
            await contexto.SaveChangesAsync();
        }

        await GuardarAsync("Finanzas del cliente", "finanzas@cliente.test", []);

        var agendaCliente = await LeerAgendaAsync();
        var agendaCentro = await LeerAgendaAsync(TipoPropietarioAgenda.Centro, centroId);

        agendaCliente.Should().ContainSingle().Which.Nombre.Should().Be("Finanzas del cliente");
        agendaCentro.Should().ContainSingle().Which.Nombre.Should().Be("Jefe de obra");
    }

    private async Task<Guid> GuardarAsync(
        string nombre, string email, IReadOnlyList<Guid> tipoDocumentoIds, Guid? contactoId = null, string? telefono = null)
    {
        await using var contexto = CrearContexto();
        var handler = new GuardarContactoAgendaCommandHandler(
            new ContactoAgendaRepository(contexto), contexto, new AlcanceDatosServiceFalso(), contexto);

        var resultado = await handler.Handle(
            new GuardarContactoAgendaCommand(
                TipoPropietarioAgenda.Cliente, _clienteId, contactoId, nombre, email, telefono,
                Cargo: null, Notas: null, EsPredeterminado: false, RecibeProgramacionVisitas: false,
                RecibeFacturacion: false, tipoDocumentoIds, Roles: []),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        return resultado.Valor;
    }

    private async Task<IReadOnlyList<ContactoAgendaDto>> LeerAgendaAsync(
        TipoPropietarioAgenda tipo = TipoPropietarioAgenda.Cliente, Guid? propietarioId = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAgendaContactosQueryHandler(contexto, contexto, new AlcanceDatosServiceFalso());

        return await handler.Handle(
            new ObtenerAgendaContactosQuery(tipo, propietarioId ?? _clienteId), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

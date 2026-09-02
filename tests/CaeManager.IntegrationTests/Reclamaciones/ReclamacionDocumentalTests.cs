using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Contactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Eventos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
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

        var cliente = Empresa.CrearComoCliente("Reclamación Test S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista de Prueba S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro de prueba");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Reclamación", "Documento Prueba", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipo = new TipoDocumento("Certificado médico", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
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

        await SembrarContactoPredeterminadoAsync("agenda@cliente.test");

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura);

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);

        var lote = lotes.Should().ContainSingle(l => l.ClienteId == _clienteId).Subject;
        lote.Documentos.Should().ContainSingle();
        lote.Destinatarios.Should().ContainSingle().Which.Email.Should().Be("agenda@cliente.test");
        lote.UltimaReclamacionFechaUtc.Should().BeNull();
    }

    [Fact]
    public async Task Filtrar_por_CentroId_acota_el_lote_a_ese_centro_aunque_el_cliente_tenga_otros_documentos_pendientes()
    {
        // Disparo desde Centro 360: el mismo Cliente tiene un segundo Centro
        // con su propio documento pendiente — filtrar por CentroId no debe
        // arrastrar lo del otro Centro, solo lo que ese Centro concreto puede
        // "reclamar con un clic".
        Guid centroId, otroCentroId;
        await using (var contexto = CrearContexto())
        {
            // La consultora "cliente" también vive en Empresas desde F3b: hay que
            // excluirla para quedarnos con la contratista real sembrada en InitializeAsync.
            var empresa = await contexto.Empresas.SingleAsync(e => e.Id != _clienteId);
            var centro = new Centro(_clienteId, empresa.Id, "Centro A");
            var otroCentro = new Centro(_clienteId, empresa.Id, "Centro B");
            contexto.Centros.AddRange(centro, otroCentro);
            await contexto.SaveChangesAsync();
            centroId = centro.Id;
            otroCentroId = otroCentro.Id;

            var otroTrabajador = Trabajador.DeEmpresa(empresa.Id, "Otro", "Trabajador", "11223344B");
            contexto.Trabajadores.Add(otroTrabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(_trabajadorId, centroId, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Asignaciones.Add(new Asignacion(otroTrabajador.Id, otroCentroId, DateOnly.FromDateTime(DateTime.UtcNow)));
            await contexto.SaveChangesAsync();

            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                otroTrabajador.Id, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura);

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(CentroId: centroId), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Which;
        lote.ClienteId.Should().Be(_clienteId);
        lote.Documentos.Should().ContainSingle().Which.TrabajadorId.Should().Be(_trabajadorId);
    }

    /// <summary>
    /// Eje del selector de lote (FiltroLoteDocumental, DEC-7): "todos los
    /// documentos de un trabajador en concreto" pasa TrabajadorId, no
    /// ClienteId — un mismo Cliente con dos trabajadores pendientes solo debe
    /// devolver el documento del elegido.
    /// </summary>
    [Fact]
    public async Task Filtrar_por_TrabajadorId_acota_el_lote_a_ese_trabajador_aunque_el_cliente_tenga_otros_pendientes()
    {
        Guid otroTrabajadorId;
        await using (var contexto = CrearContexto())
        {
            var centro = await contexto.Centros.SingleAsync();
            var empresa = await contexto.Empresas.SingleAsync(e => e.Id != _clienteId);
            var otroTrabajador = Trabajador.DeEmpresa(empresa.Id, "Otro", "Trabajador", "11223344B");
            contexto.Trabajadores.Add(otroTrabajador);
            await contexto.SaveChangesAsync();
            otroTrabajadorId = otroTrabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(otroTrabajadorId, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                otroTrabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura);

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(TrabajadorId: _trabajadorId), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Which;
        lote.Documentos.Should().ContainSingle().Which.TrabajadorId.Should().Be(_trabajadorId);
    }

    /// <summary>"Todos los EPIs de todos los trabajadores" (DEC-7): TipoDocumentoIds acota por tipo sin acotar por titular ni trabajador.</summary>
    [Fact]
    public async Task Filtrar_por_TipoDocumentoIds_excluye_documentos_de_otros_tipos_del_mismo_trabajador()
    {
        Guid otroTipoId;
        await using (var contexto = CrearContexto())
        {
            var otroTipo = new TipoDocumento("EPI", 12, aplicaVencimientoAutomatico: true, 2, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            contexto.TiposDocumento.Add(otroTipo);
            await contexto.SaveChangesAsync();
            otroTipoId = otroTipo.Id;

            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, otroTipoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearQueryHandler(lectura);

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(TipoDocumentoIds: [otroTipoId]), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Which;
        lote.Documentos.Should().ContainSingle().Which.TipoDocumentoId.Should().Be(otroTipoId);
    }

    /// <summary>
    /// El lote de la cola (DrawerReclamacionLote, selector tipo × ámbito) no
    /// puede actuar sobre lo que un Gestor CAE acotado a su cartera no podía
    /// ver — misma propiedad que protege el resto de la aplicación
    /// (IAlcanceDatosService). Ejercita ObtenerLoteReclamacionPorFiltroQueryHandler
    /// completo (dispatcher → ObtenerLoteReclamacionQuery → EF → Postgres),
    /// no solo la query interna, porque el dispatcher es el camino nuevo que
    /// introduce el selector — "hereda el acotado por delegación" es una
    /// suposición hasta que algo lo demuestra.
    /// </summary>
    [Fact]
    public async Task El_lote_por_filtro_no_incluye_clientes_fuera_de_la_cartera_del_gestor()
    {
        Guid clienteFueraDeCarteraId, trabajadorFueraDeCarteraId;
        await using (var contexto = CrearContexto())
        {
            var clienteFuera = Empresa.CrearComoCliente("Fuera de Cartera S.L.", GenerarCifValido(900), false, null, null);
            var empresaFuera = new Empresa("Contratista Fuera S.L.", GenerarCifValido(901));
            contexto.Empresas.AddRange(clienteFuera, empresaFuera);
            await contexto.SaveChangesAsync();
            clienteFueraDeCarteraId = clienteFuera.Id;

            var centroFuera = new Centro(clienteFuera.Id, empresaFuera.Id, "Centro fuera de cartera");
            contexto.Centros.Add(centroFuera);

            var trabajadorFuera = Trabajador.DeEmpresa(empresaFuera.Id, "Fuera", "De Cartera", GenerarDniValido(900));
            contexto.Trabajadores.Add(trabajadorFuera);
            await contexto.SaveChangesAsync();
            trabajadorFueraDeCarteraId = trabajadorFuera.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorFuera.Id, centroFuera.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajadorFuera.Id, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
            contexto.ContactosAgenda.Add(ContactoAgenda.DeCliente(
                clienteFuera.Id, "Agenda fuera", "fuera@cliente.test", esPredeterminado: true));
            await contexto.SaveChangesAsync();
        }

        // Gestor CAE acotado a _clienteId — clienteFueraDeCarteraId nunca aparece en su lista visible.
        var alcanceAcotado = new AlcanceDatosServiceFalso(clienteIds: [_clienteId]);

        await using var lectura = CrearContexto();
        var queryHandler = CrearQueryHandler(lectura, alcanceAcotado);
        var dispatcher = new ObtenerLoteReclamacionPorFiltroQueryHandler(new MediatorReenviaAObtenerLoteReclamacion(queryHandler));

        // "Todos los trabajadores visibles" (EntidadId null, DEC-7): con
        // cartera acotada debe traer solo _clienteId, nunca el de fuera.
        var lotesTodos = await dispatcher.Handle(
            new ObtenerLoteReclamacionPorFiltroQuery(new FiltroLoteDocumental(AmbitoAplicacion.Trabajador, [], null)),
            CancellationToken.None);

        lotesTodos.Should().ContainSingle().Which.TitularId.Should().Be(_clienteId);
        lotesTodos.Should().NotContain(l => l.TitularId == clienteFueraDeCarteraId);

        // Pedir explícitamente un Trabajador fuera de cartera por EntidadId
        // (el caso "un trabajador en concreto" de DEC-7) tampoco debe colar
        // nada — el alcance no se puede sortear pasando el Id a mano.
        var lotesTrabajadorFuera = await dispatcher.Handle(
            new ObtenerLoteReclamacionPorFiltroQuery(new FiltroLoteDocumental(AmbitoAplicacion.Trabajador, [], trabajadorFueraDeCarteraId)),
            CancellationToken.None);

        lotesTrabajadorFuera.Should().BeEmpty("pedir un Trabajador fuera de la cartera del gestor por Id no debe devolver nada");
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
        var handler = CrearQueryHandler(lectura);

        var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);

        lotes.Should().NotContain(l => l.ClienteId == _clienteId);
    }

    [Fact]
    public async Task Sin_buzon_conectado_la_reclamacion_sale_por_email_y_no_deja_conversacion()
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

        await SembrarContactoPredeterminadoAsync("agenda@cliente.test");

        var emailServiceFalso = new EmailServiceFalso();
        var mediatorFalso = new MediatorFalso(Guid.NewGuid());
        await using (var contexto = CrearContexto())
        {
            var handler = CrearCommandHandler(contexto, emailServiceFalso, mediatorFalso);

            var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        emailServiceFalso.Enviados.Should().ContainSingle(e => e.Destinatario == "agenda@cliente.test");
        mediatorFalso.UltimoEnvio.Should().BeNull("sin buzón conectado no se pasa por Comunicaciones");
        mediatorFalso.Publicados.Should().BeEmpty("sin conversación no hay timeline al que avisar");

        await using var lectura = CrearContexto();
        var reclamacion = (await lectura.ReclamacionesDocumentales.ToListAsync()).Should().ContainSingle().Which;
        reclamacion.ConversacionId.Should().BeNull();

        var handlerConsulta = CrearQueryHandler(lectura);
        var lotes = await handlerConsulta.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);
        lotes.Should().ContainSingle(l => l.ClienteId == _clienteId).Which.UltimaReclamacionFechaUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Un_buzon_personal_de_un_gestor_nunca_se_usa_como_buzon_generico_de_la_reclamacion()
    {
        // Regresión: el buzón personal de un gestor (GestorPropietarioId) tiene
        // ClienteId null igual que el buzón genérico del tenant — sin excluirlo
        // explícitamente, la reclamación podía salir desde el correo personal
        // de un gestor cualquiera, sin que él lo supiera ni lo consintiera.
        Guid documentoId;
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1));
            contexto.Documentos.Add(documento);
            // Único buzón habilitado: personal de un gestor. Nunca debe elegirse.
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion(
                "gestor.personal@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid()));
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
        }

        await SembrarContactoPredeterminadoAsync("agenda@cliente.test");

        var emailServiceFalso = new EmailServiceFalso();
        var mediatorFalso = new MediatorFalso(Guid.NewGuid());

        await using (var contexto = CrearContexto())
        {
            var handler = CrearCommandHandler(contexto, emailServiceFalso, mediatorFalso);

            var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // Sin ningún buzón "elegible" (el único que hay es personal y ajeno),
        // cae a la rama sin buzón conectado — nunca al personal.
        mediatorFalso.UltimoEnvio.Should().BeNull("un buzón personal de otro gestor nunca es el buzón genérico");
        emailServiceFalso.Enviados.Should().ContainSingle();

        await using var lectura = CrearContexto();
        (await lectura.ReclamacionesDocumentales.ToListAsync()).Should().ContainSingle().Which.ConversacionId.Should().BeNull();
    }

    [Fact]
    public async Task Con_buzon_conectado_la_reclamacion_nace_como_conversacion_en_un_unico_envio()
    {
        Guid documentoId;
        Guid conversacionEsperada;
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId,
                DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1));
            contexto.Documentos.Add(documento);
            contexto.ConexionesIntegracion.Add(new ConexionIntegracion("cae@consultora.com", "Buzón CAE"));

            // La conversación que en producción crea EnviarMensajeNuevoCommand.
            // Tiene que existir de verdad: la FK de ReclamacionDocumental no
            // acepta un Id inventado, que es justo lo que debe garantizar.
            var conversacion = new Conversacion("Documentación pendiente", _clienteId);
            contexto.Conversaciones.Add(conversacion);

            await contexto.SaveChangesAsync();
            documentoId = documento.Id;
            conversacionEsperada = conversacion.Id;
        }

        await SembrarContactoPredeterminadoAsync("contacto@cliente.test", "prl@cliente.test");

        var emailServiceFalso = new EmailServiceFalso();
        var mediatorFalso = new MediatorFalso(conversacionEsperada);

        await using (var contexto = CrearContexto())
        {
            var handler = CrearCommandHandler(contexto, emailServiceFalso, mediatorFalso);

            var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // Un solo envío con los dos destinatarios, no uno por destinatario: el
        // lote es una conversación, no dos hilos paralelos.
        emailServiceFalso.Enviados.Should().BeEmpty("con buzón conectado la salida va por Comunicaciones");
        var envio = mediatorFalso.UltimoEnvio.Should().NotBeNull().And.Subject.As<EnviarMensajeNuevoCommand>();
        envio.Destinatarios.Should().BeEquivalentTo(["contacto@cliente.test", "prl@cliente.test"]);
        envio.ClienteId.Should().Be(_clienteId);
        mediatorFalso.VecesLlamado.Should().Be(1);

        await using var lectura = CrearContexto();
        var reclamacion = (await lectura.ReclamacionesDocumentales.ToListAsync()).Should().ContainSingle().Which;
        reclamacion.ConversacionId.Should().Be(conversacionEsperada);
        reclamacion.DestinatarioEmail.Should().Be("contacto@cliente.test; prl@cliente.test");

        // El timeline se entera del envío, y con la reclamación ya persistida.
        var publicado = mediatorFalso.Publicados.Should().ContainSingle().Which.Should().BeOfType<ReclamacionEnviadaEvent>().Subject;
        publicado.ConversacionId.Should().Be(conversacionEsperada);
        publicado.ReclamacionId.Should().Be(reclamacion.Id);
    }

    [Fact]
    public async Task Sin_ningun_contacto_en_la_agenda_falla_al_enviar()
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
        var handler = CrearCommandHandler(contexto2, new EmailServiceFalso(), new MediatorFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EnviarReclamacionCommand(_clienteId, [documentoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDestinatario");
    }

    /// <summary>
    /// Regresión N+1: con CentroId null (caso /documentos), Handle() agrupaba
    /// las filas por Cliente y llamaba a
    /// IResolucionDestinatariosAgendaService.ResolverAsync UNA VEZ POR
    /// CLIENTE dentro del bucle -- una consultora con muchos clientes con
    /// documentación pendiente disparaba 2 consultas SQL más (contactos +
    /// vínculos) por cada Cliente adicional del lote. ResolverParaClientesAsync
    /// resuelve todos los Clientes del lote con las mismas 2 consultas
    /// agrupadas sobre la unión -- el número de comandos SQL del Handle
    /// completo no debe crecer con el número de Clientes.
    /// </summary>
    [Fact]
    public async Task Resolver_destinatarios_del_lote_no_escala_en_consultas_sql_con_el_numero_de_clientes()
    {
        var comandosCon2Clientes = await ContarComandosSqlDelLoteAsync(2);
        var comandosCon6Clientes = await ContarComandosSqlDelLoteAsync(6);

        comandosCon6Clientes.Should().Be(
            comandosCon2Clientes,
            "el número de comandos SQL de Handle() no debe depender de cuántos Clientes tengan documentación pendiente en el lote");
    }

    /// <summary>
    /// Siembra <paramref name="numeroClientes"/> Clientes independientes, cada
    /// uno con un Centro, un Trabajador con Asignación activa, un Documento
    /// reclamable dentro de la ventana y un contacto de agenda predeterminado
    /// propio -- así cada Cliente aparece en un grupo distinto del lote (caso
    /// CentroId null) -- y devuelve cuántos comandos SQL reales ejecuta
    /// Handle() completo, contados con un DbCommandInterceptor.
    /// </summary>
    private static async Task<int> ContarComandosSqlDelLoteAsync(int numeroClientes)
    {
        var cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        var tenant = Guid.NewGuid();

        try
        {
            await using (var setup = CrearContexto(cadenaConexion, tenant))
            {
                await setup.Database.MigrateAsync();
                setup.ParametrosSistema.Add(new ParametroSistema(30, 15));
                await setup.SaveChangesAsync();

                for (var i = 0; i < numeroClientes; i++)
                {
                    var cliente = Empresa.CrearComoCliente($"Reclamación Bulk {i} S.L.", GenerarCifValido(i * 2), false, null, null);
                    var empresa = new Empresa($"Contratista Bulk {i} S.L.", GenerarCifValido(i * 2 + 1));
                    setup.Empresas.Add(cliente);
                    setup.Empresas.Add(empresa);
                    await setup.SaveChangesAsync();

                    var centro = new Centro(cliente.Id, empresa.Id, $"Centro Bulk {i}");
                    setup.Centros.Add(centro);

                    var trabajador = Trabajador.DeEmpresa(empresa.Id, $"Bulk{i}", "Trabajador", GenerarDniValido(i));
                    setup.Trabajadores.Add(trabajador);

                    var tipo = new TipoDocumento($"Tipo Bulk {i}", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
                    setup.TiposDocumento.Add(tipo);
                    await setup.SaveChangesAsync();

                    setup.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
                    setup.Documentos.Add(Documento.DeTrabajador(
                        trabajador.Id, tipo.Id,
                        DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-10), DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(1)));
                    setup.ContactosAgenda.Add(ContactoAgenda.DeCliente(
                        cliente.Id, $"Agenda Bulk {i}", $"agenda-bulk-{i}@cliente.test", esPredeterminado: true));
                    await setup.SaveChangesAsync();
                }
            }

            var contador = new ContadorComandosInterceptor();
            await using var lectura = CrearContexto(cadenaConexion, tenant, contador);

            var handler = new ObtenerLoteReclamacionQueryHandler(
                lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura,
                new AlcanceDatosServiceFalso(), new ResolucionDestinatariosAgendaService(lectura, lectura));

            var lotes = await handler.Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);

            lotes.Should().HaveCount(numeroClientes);
            lotes.Should().OnlyContain(l => l.Destinatarios!.Count == 1);

            return contador.NumeroDeComandos;
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(cadenaConexion);
        }
    }

    /// <summary>CIF con dígito de control real (organización "B"), mismo algoritmo que AislamientoPorAgregadoTests.GenerarCifValido pero indexado para no colisionar dentro de un mismo lote.</summary>
    private static string GenerarCifValido(int indice)
    {
        var digitos = (indice + 1).ToString().PadLeft(7, '0');
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>DNI con letra de control real (algoritmo estándar módulo 23), indexado igual que GenerarCifValido.</summary>
    private static string GenerarDniValido(int indice)
    {
        const string letras = "TRWAGMYFPDXBNJZSQVHLCKE";
        var numero = indice + 1;
        return $"{numero:D8}{letras[numero % 23]}";
    }

    private static CaeManagerDbContext CrearContexto(string cadenaConexion, Guid tenant, params IInterceptor[] interceptoresAdicionales)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual));

        if (interceptoresAdicionales.Length > 0)
            builder = builder.AddInterceptors(interceptoresAdicionales);

        return new CaeManagerDbContext(builder.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ContadorComandosInterceptor : DbCommandInterceptor
    {
        public int NumeroDeComandos { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            NumeroDeComandos++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            NumeroDeComandos++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Servicio de resolución REAL, no un doble: los destinatarios salen ahora
    // de la agenda de contactos, y lo que estos tests tienen que comprobar es
    // justamente esa resolución contra datos reales.
    private static ObtenerLoteReclamacionQueryHandler CrearQueryHandler(CaeManagerDbContext contexto) =>
        CrearQueryHandler(contexto, new AlcanceDatosServiceFalso());

    private static ObtenerLoteReclamacionQueryHandler CrearQueryHandler(CaeManagerDbContext contexto, IAlcanceDatosService alcanceDatos) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            alcanceDatos, new ResolucionDestinatariosAgendaService(contexto, contexto));

    /// <summary>
    /// Reenvía el único tipo de Request que ObtenerLoteReclamacionPorFiltroQueryHandler
    /// despacha por Ambito=Trabajador al handler REAL (no un doble que
    /// devuelve una respuesta enlatada) — así el test ejercita la cadena
    /// completa dispatcher → query → EF → Postgres, no solo la traducción
    /// del filtro (eso ya lo cubre ObtenerLoteReclamacionPorFiltroQueryHandlerTests
    /// en Application.Tests, con MediatorFalso).
    /// </summary>
    private sealed class MediatorReenviaAObtenerLoteReclamacion(ObtenerLoteReclamacionQueryHandler handler) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerLoteReclamacionQuery query)
                return (Task<TResponse>)(object)handler.Handle(query, cancellationToken);

            throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static EnviarReclamacionCommandHandler CrearCommandHandler(
        CaeManagerDbContext contexto, IEmailService emailService, IMediator mediator) =>
        CrearCommandHandler(contexto, emailService, mediator, new AlcanceDatosServiceFalso());

    private static EnviarReclamacionCommandHandler CrearCommandHandler(
        CaeManagerDbContext contexto, IEmailService emailService, IMediator mediator, IAlcanceDatosService alcanceDatos) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto,
            alcanceDatos, new ResolucionDestinatariosAgendaService(contexto, contexto),
            CrearRegistroEnvio(contexto, emailService, mediator));

    /// <summary>
    /// Cola común de envío (buzón → correo → registro → evento), compartida
    /// por los dos comandos de reclamación — servicio REAL, no un doble: es
    /// justo la parte cuyo comportamiento estos tests comprueban.
    /// </summary>
    private static RegistroEnvioReclamacionService CrearRegistroEnvio(
        CaeManagerDbContext contexto, IEmailService emailService, IMediator mediator) =>
        new(contexto, emailService, new ReclamacionDocumentalRepository(contexto),
            new CurrentUserServiceFalso(Guid.NewGuid()), mediator, contexto);

    /// <summary>Contacto predeterminado de la agenda del Cliente — el que recibe lo que no tiene dueño explícito.</summary>
    private async Task SembrarContactoPredeterminadoAsync(params string[] emails)
    {
        await using var contexto = CrearContexto();
        foreach (var email in emails)
        {
            contexto.ContactosAgenda.Add(ContactoAgenda.DeCliente(
                _clienteId, email, email, esPredeterminado: true));
        }
        await contexto.SaveChangesAsync();
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


    private class EmailServiceFalso : IEmailService
    {
        public List<(string Destinatario, string Asunto)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, CancellationToken cancellationToken = default)
        {
            Enviados.Add((destinatarioEmail, asunto));
            return Task.FromResult(Result.Exito());
        }
    }

    /// <summary>
    /// Solo cubre <see cref="EnviarMensajeNuevoCommand"/>: cualquier otro
    /// request revienta a propósito, para que el día que la reclamación empiece
    /// a componer otro Command el test lo diga en vez de pasar en silencio.
    /// </summary>
    private class MediatorFalso(Guid conversacionIdADevolver) : IMediator
    {
        public EnviarMensajeNuevoCommand? UltimoEnvio { get; private set; }
        public int VecesLlamado { get; private set; }
        public List<INotification> Publicados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand envio)
                throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

            UltimoEnvio = envio;
            VecesLlamado++;
            return Task.FromResult((TResponse)(object)Result.Exito(conversacionIdADevolver));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }
    }
}

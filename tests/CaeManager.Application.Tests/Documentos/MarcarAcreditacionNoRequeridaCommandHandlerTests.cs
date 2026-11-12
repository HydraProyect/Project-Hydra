using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Esta plataforma no lo pide» de la ventana de una Gestión (DECISION_LOG 2026-11-08, punto 3): deja la acreditación
/// No requerida, nunca toca el Documento, y solo dentro de la Asignación de Cartera de quien lo pide. El rol lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest, TResponse}"/>, no la pantalla.
/// </summary>
public class MarcarAcreditacionNoRequeridaCommandHandlerTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Aceptada)]
    [InlineData(EstadoAcreditacion.Rechazada)]
    public async Task Desde_cualquier_estado_la_deja_no_requerida_y_guarda_una_vez(EstadoAcreditacion estado)
    {
        var escenario = Escenario.Nuevo();
        escenario.Poner(estado);

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
        escenario.Documento.SustituidoEnUtc.Should().BeNull("el Documento en TALVEG no cambia");
    }

    [Fact]
    public async Task Desde_validada_con_una_fecha_vencida_borra_la_vigencia_para_no_poner_el_Centro_en_rojo()
    {
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1)));

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        escenario.Acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        escenario.Acreditacion.FechaVencimientoEnPlataforma.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Si_el_paso_cambio_desde_que_se_vio_no_lo_sobrescribe_a_ciegas()
    {
        // Se abrió la Gestión con el paso Pendiente de subir; entretanto otro Gestor CAE lo marcó rechazado por el portal.
        var escenario = Escenario.Nuevo();
        var vista = escenario.Acreditacion.Version;
        escenario.Poner(EstadoAcreditacion.Rechazada);
        escenario.GuardadoPorOtro();

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, vista), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada, "el rechazo no sale de la cola sin aviso");
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
        escenario.Acreditaciones.LecturasActualizadas.Should().Be(1,
            "compara contra la base releída: con una entidad rastreada obsoleta la guarda no vería el cambio");
    }

    [Fact]
    public async Task Una_vigencia_confirmada_despues_de_abrir_la_Gestion_sin_cambiar_el_estado_no_se_borra()
    {
        // Hallazgo ALTO de Codex (r1): con solo el estado visto, la comparación pasaba y «no lo pide» borraba esta fecha.
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        var vista = escenario.Acreditacion.Version;
        var confirmada = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 6, 30));
        escenario.Acreditacion.ConfirmarVigencia(confirmada);
        escenario.GuardadoPorOtro();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada, "precondición: el estado no cambió");

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, vista), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        escenario.Acreditacion.Vigencia.Should().Be(confirmada, "la vigencia anotada por otra persona sigue ahí");
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_estado_que_cambio_y_volvio_al_que_se_vio_tampoco_se_sobrescribe()
    {
        // Pendiente → Subida → Pendiente: el estado coincide con el visto, pero ha habido cambios que no se han visto.
        var escenario = Escenario.Nuevo();
        var vista = escenario.Acreditacion.Version;
        escenario.Acreditacion.MarcarSubida();
        escenario.GuardadoPorOtro();
        escenario.Acreditacion.DevolverAPendienteDeSubir();
        escenario.GuardadoPorOtro();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "precondición");

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, vista), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_segundo_rechazo_sobre_una_ya_rechazada_tampoco_se_sobrescribe()
    {
        // Ronda puente: el segundo rechazo no cambia ninguna columna de la raíz (el motivo va en otra tabla). Sin interceptor
        // simulado: es el dominio quien renueva la versión.
        var escenario = Escenario.Nuevo();
        escenario.Poner(EstadoAcreditacion.Rechazada);
        var vista = escenario.Acreditacion.Version;
        escenario.Acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Falta la firma del médico", DateTime.UtcNow);

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, vista), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_acreditacion_de_un_Documento_ya_sustituido_no_se_marca_aunque_la_version_coincida(bool yaNoRequerida)
    {
        // Revisión puente (r2): renovar con archivo nuevo no toca las acreditaciones del anterior, así que su versión no
        // cambia y la pantalla abierta antes la marcaría. Es historial (D5/D8): ni se escribe ni se responde éxito.
        var escenario = Escenario.Nuevo();
        if (yaNoRequerida) escenario.Acreditacion.MarcarNoRequerida();
        var estadoAntes = escenario.Acreditacion.Estado;
        var nuevo = escenario.Documento.NuevoDelMismoTitular(
            new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca, "tenant/renovado.pdf", null, null);
        escenario.Documento.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.EnHistorial");
        escenario.Acreditacion.Estado.Should().Be(estadoAntes);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_sustitucion_que_solo_ve_la_relectura_del_Documento_basta_para_negarse_con_el_historial()
    {
        // Hallazgo 4 (33b156b78): la instancia rastreada del circuito sigue operativa; la sustitución confirmada por otra
        // sesión solo aparece al releer de la base (CambioEnLaBase lo ve solo la lectura «…Actualizado», como EF).
        var escenario = Escenario.Nuevo();
        var nuevo = escenario.Documento.NuevoDelMismoTitular(
            new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca, "tenant/renovado.pdf", null, null);
        escenario.Documentos.CambioEnLaBase = d =>
        {
            if (DocumentoOperativo.Es(d))
                d.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        };

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Documento.EnHistorial");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_sustitucion_confirmada_entre_la_guarda_y_el_guardado_hace_fallar_con_el_historial_y_deshace_la_marca()
    {
        // Codex r3 (MEDIO) sobre f83535b9e: la guarda vio el Documento operativo y otra sesión lo sustituye antes del
        // guardado. «No lo pide» no escribe el Documento, así que ningún token lo para en el guardado: sin la comprobación
        // final, bajo el bloqueo y dentro de la transacción, respondía éxito con la marca sobre el historial.
        var escenario = Escenario.Nuevo();
        var nuevo = escenario.Documento.NuevoDelMismoTitular(
            new DateOnly(2026, 2, 1), VigenciaDocumento.NoCaduca, "tenant/renovado.pdf", null, null);
        escenario.UnitOfWork.AlGuardar = () =>
            escenario.Documento.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        escenario.UnitOfWork.VecesGuardado.Should().Be(1,
            "control del instrumento: la guarda dejó pasar y la sustitución entró entre ella y el guardado");
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.EnHistorial");
        escenario.Documentos.LecturasBloqueadas.Should().Be(1);
        escenario.Transaccion.Deshechas.Should().Be(1, "el fallo deshace la marca ya guardada");
        escenario.Transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_Documento_eliminado_entre_la_guarda_y_el_guardado_tambien_deshace_la_marca()
    {
        var escenario = Escenario.Nuevo();
        escenario.UnitOfWork.AlGuardar = () => escenario.Documento.MarcarComoEliminado(Guid.NewGuid());

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "control del instrumento: la eliminación entró tras la guarda");
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task Sin_cambios_entre_medias_la_comprobacion_bloqueada_deja_confirmar()
    {
        // Control positivo de los dos anteriores: la misma comprobación final, con el Documento todavía vigente.
        var escenario = Escenario.Nuevo();

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Documentos.LecturasBloqueadas.Should().Be(1, "la decisión final se toma con la fila bloqueada");
        escenario.Transaccion.Ejecutadas.Should().Be(1);
        escenario.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Con_una_version_vacia_falla_y_no_escribe()
    {
        // Guid.Empty es «sin comprobación» en ConcurrenciaOptimista: aquí, aunque se saltara el validador, no pasa.
        var escenario = Escenario.Nuevo();

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, Guid.Empty), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Ya_no_requerida_no_escribe_nada()
    {
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarNoRequerida();
        escenario.GuardadoPorOtro();

        // Otra persona la marcó antes con la misma intención: no hay nada que pisar, aunque la versión vista sea otra.
        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_no_requerida_historica_con_vigencia_no_cuenta_como_hecha_y_marcarla_le_quita_la_fecha()
    {
        // Fila anterior a la invariante (antes se podía anotar vigencia sobre una No requerida).
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1)));
        typeof(AcreditacionDocumentoPlataforma).GetProperty(nameof(AcreditacionDocumentoPlataforma.Estado))!
            .SetValue(escenario.Acreditacion, EstadoAcreditacion.NoRequerida);

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        escenario.Acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_el_Centro_del_acceso_fuera_de_su_cartera_falla_como_si_no_existiera_y_no_escribe()
    {
        // La cartera tiene otro Centro, que también tiene acceso de plataforma: la guarda mira el Centro de ESTE acceso.
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarSubida();
        var otroCentroId = Guid.NewGuid();
        escenario.Centros.ListaCanalesGestionDocumental.Add(
            CanalGestionDocumental.DePlataforma(otroCentroId, "Portal", escenario.ProveedorId, null, null, null));
        var fueraDeCartera = new AlcanceDatosServiceFalso(centroIdsParaGestion: [otroCentroId]);

        var resultado = await escenario.Handler(fueraDeCartera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_Centro_en_cartera_pero_el_Documento_no_visible_tampoco_escribe()
    {
        var escenario = Escenario.Nuevo();
        var documentoFuera = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()], centroIdsParaGestion: [escenario.CentroId]);

        var resultado = await escenario.Handler(documentoFuera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_Centro_del_acceso_en_su_cartera_si_escribe()
    {
        // Control positivo del caso anterior: la misma cartera, con el Centro de este acceso.
        var escenario = Escenario.Nuevo();
        var enCartera = new AlcanceDatosServiceFalso(centroIdsParaGestion: [escenario.CentroId]);

        var resultado = await escenario.Handler(enCartera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
    }

    [Fact]
    public async Task Falla_cuando_la_acreditacion_no_existe()
    {
        var escenario = Escenario.Nuevo();

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_la_acreditacion()
    {
        var validador = new MarcarAcreditacionNoRequeridaCommandValidator();

        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.Empty, Guid.NewGuid())).IsValid.Should().BeFalse();
        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), Guid.NewGuid())).IsValid.Should().BeTrue();
        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), Guid.Empty)).IsValid.Should()
            .BeFalse("sin versión vista no hay con qué comparar");
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Ni_Consulta_ni_Cliente_ni_un_rol_desconocido_llegan_al_handler(string? rol)
    {
        var (resultado, llego) = await EjecutarConRolAsync(rol);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        llego.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Gestor_CAE_si_llega_al_handler()
    {
        var (resultado, llego) = await EjecutarConRolAsync("GestorCae");

        resultado.EsExitoso.Should().BeTrue();
        llego.Should().BeTrue();
    }

    private static async Task<(Result Resultado, bool Llego)> EjecutarConRolAsync(string? rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<MarcarAcreditacionNoRequeridaCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), Guid.NewGuid()),
            _ =>
            {
                llego = true;
                return Task.FromResult(Result.Exito());
            },
            CancellationToken.None);

        return (resultado, llego);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class Escenario
    {
        public required Documento Documento { get; init; }
        public required AcreditacionDocumentoPlataforma Acreditacion { get; init; }
        public required Guid CentroId { get; init; }
        public required Guid ProveedorId { get; init; }
        public CentrosQueryContextFalso Centros { get; } = new();
        public DocumentoRepositorioFalso Documentos { get; } = new();
        public AcreditacionDocumentoPlataformaRepositorioFalso Acreditaciones { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();

        public static Escenario Nuevo()
        {
            var centroId = Guid.NewGuid();
            var proveedor = new ProveedorPlataformaCae("nalanda", "Nalanda");
            var canal = CanalGestionDocumental.DePlataforma(centroId, "Portal", proveedor.Id, null, null, null);
            var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            var escenario = new Escenario
            {
                Documento = documento,
                Acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id),
                CentroId = centroId,
                ProveedorId = proveedor.Id
            };
            escenario.Centros.ListaCanalesGestionDocumental.Add(canal);
            escenario.Documentos.Agregar(documento);
            escenario.Acreditaciones.Agregar(escenario.Acreditacion);
            return escenario;
        }

        public void Poner(EstadoAcreditacion estado)
        {
            switch (estado)
            {
                case EstadoAcreditacion.Subida: Acreditacion.MarcarSubida(); break;
                case EstadoAcreditacion.Aceptada: Acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui); break;
                case EstadoAcreditacion.Rechazada: Acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow); break;
            }

            Acreditacion.Estado.Should().Be(estado, "el escenario parte del estado pedido");
        }

        /// <summary>
        /// Lo que hace <c>ConcurrenciaOptimistaInterceptor</c> al guardar otra persona una modificación: renueva la versión.
        /// Sin él (aquí no hay EF), el cambio de dominio no la movería.
        /// </summary>
        public void GuardadoPorOtro() =>
            typeof(EntidadBase).GetProperty(nameof(EntidadBase.Version))!.SetValue(Acreditacion, Guid.NewGuid());

        public MarcarAcreditacionNoRequeridaCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
            new(Acreditaciones, Documentos, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), Centros, UnitOfWork, Transaccion);
    }
}

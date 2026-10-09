using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.ApoyoCartera;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones.ApoyoCartera;

/// <summary>
/// La propuesta de apoyo (ADR-011 § 2.7, enmienda 2026-10-08): quién propone, a quién, quién
/// acepta, rechaza y retira, y qué se vuelve a decidir al aceptar. Las negativas de
/// autorización viven aquí; que la política RLS aísle la tabla, que la cartera emitida no sea
/// principal en la base de datos y las carreras de dos conexiones los prueba
/// <c>PropuestaApoyoCarteraBajoRuntimeTests</c> en integración.
/// </summary>
public class PropuestaApoyoCarteraTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid GestorA = Guid.NewGuid(); // principal de partida
    private static readonly Guid GestorB = Guid.NewGuid(); // sin cartera: a quien se propone
    private static readonly Guid GestorC = Guid.NewGuid(); // ya tiene cartera de apoyo
    private static readonly Guid GestorD = Guid.NewGuid(); // sin cartera, tercero
    private static readonly Guid Coordinador = Guid.NewGuid();
    private static readonly Guid Ajeno = Guid.NewGuid(); // Gestor CAE de otro Operador CAE

    /// <summary>Directorio que anota cada lectura de cuenta, para ordenarla respecto del candado.</summary>
    private sealed class DirectorioConTraza(DirectorioRolesEnOrigen real, List<string> eventos) : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            real.EsVisibleEnTenantActualAsync(usuarioId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            real.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            real.ObtenerTenantDeUsuarioAsync(usuarioId, cancellationToken);

        public Task<bool> EsCuentaActivaConRolAsync(
            Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default)
        {
            eventos.Add($"cuenta:{usuarioId}");
            return real.EsCuentaActivaConRolAsync(usuarioId, tenantId, rol, cancellationToken);
        }
    }

    private sealed class GestoresCaeFalsos : IDirectorioCompanerosGestorCae
    {
        public List<(Guid OperadorTenantId, CompaneroGestorCaeDto Gestor)> Gestores { get; } = [];

        public Task<IReadOnlyList<CompaneroGestorCaeDto>> ObtenerGestoresCaeDelOperadorAsync(
            Guid operadorTenantId, Guid excluirUsuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CompaneroGestorCaeDto>>(Gestores
                .Where(g => g.OperadorTenantId == operadorTenantId && g.Gestor.UsuarioId != excluirUsuarioId)
                .Select(g => g.Gestor)
                .ToList());
    }

    private sealed class Escenario
    {
        public Tenant Empresa { get; } = new("Talleres Norte Demo");
        public AsignacionOperacion Operacion { get; }
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public PropuestaApoyoCarteraRepositorioFalso Repositorio { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public List<string> Eventos { get; } = [];
        public BloqueoCarteraUsuarioFalso Bloqueo { get; }
        public DirectorioRolesEnOrigen Roles { get; } = new();
        public TenantsQueryContextFalso Tenants { get; } = new();
        public GestoresCaeFalsos GestoresCae { get; } = new();

        public Escenario()
        {
            Bloqueo = new BloqueoCarteraUsuarioFalso { Eventos = Eventos };
            Operacion = AsignacionOperacion.Externa(
                Empresa.Id, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);
            Tenants.ListaTenants.Add(Empresa);
            Catalogo.RegistrarAsignable(Operador, Operacion, Empresa.Nombre);

            foreach (var gestor in new[] { GestorA, GestorB, GestorC, GestorD })
            {
                Roles.Asignar(gestor, Operador, "GestorCae");
                GestoresCae.Gestores.Add((Operador, new CompaneroGestorCaeDto(gestor, $"Gestor {gestor:N}"[..14], null, null)));
            }

            Roles.Asignar(Coordinador, Operador, "CoordinadorCae");
            Roles.Asignar(Ajeno, OtroOperador, "GestorCae");
            GestoresCae.Gestores.Add((OtroOperador, new CompaneroGestorCaeDto(Ajeno, "Gestor ajeno", null, null)));

            Cartera(GestorA, principal: true);
            Cartera(GestorC);
            // Sin el Tenant en su cartera: son candidatos a recibirlo.
            Catalogo.RegistrarCandidato(Operador, GestorB, Operacion, Empresa.Nombre);
            Catalogo.RegistrarCandidato(Operador, GestorD, Operacion, Empresa.Nombre);
            Catalogo.RegistrarCandidato(Operador, Coordinador, Operacion, Empresa.Nombre);
        }

        public void Cartera(Guid usuarioId, bool principal = false, string rol = "GestorCae") =>
            Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
                Operacion.Id, Empresa.Id, Empresa.Nombre, usuarioId, rol, principal, null)));

        /// <summary>Pasa la marca de principal a otra persona, que ya tiene cartera viva.</summary>
        public void PasarLaMarcaA(Guid usuarioId)
        {
            for (var i = 0; i < Catalogo.CarterasVivas.Count; i++)
            {
                var (operador, cartera) = Catalogo.CarterasVivas[i];
                Catalogo.CarterasVivas[i] = (operador, cartera with { EsPrincipal = cartera.UsuarioId == usuarioId });
            }
        }

        public Guid? Principal => Catalogo.CarterasVivas
            .Where(c => c.Cartera.EsPrincipal).Select(c => (Guid?)c.Cartera.UsuarioId).SingleOrDefault();

        /// <summary>
        /// La sesión de <paramref name="usuarioId"/>: su rol sale de Identity (<see cref="Roles"/>);
        /// <paramref name="rolDeSesion"/> es el claim, que dentro de un Workspace operativo derivado es el
        /// de la cartera y no decide nada.
        /// </summary>
        public CurrentUserServicePorAmbito Como(Guid usuarioId, Guid? origen = null, string? rolDeSesion = "GestorCae") =>
            new(usuarioId, origen ?? Operador, rolDeSesion, rolDeSesion);

        public Task<Result<Guid>> Proponer(Guid actor, Guid destinatario, Guid? operacion = null, Guid? origen = null, string? rolDeSesion = "GestorCae") =>
            new ProponerApoyoCarteraCommandHandler(Como(actor, origen, rolDeSesion), Roles, Catalogo, Repositorio)
                .Handle(new ProponerApoyoCarteraCommand(operacion ?? Operacion.Id, destinatario), default);

        public Task<Result> Aceptar(Guid actor, Guid propuestaId, Guid? origen = null, string? rolDeSesion = "GestorCae") =>
            new AceptarPropuestaApoyoCarteraCommandHandler(
                    Como(actor, origen, rolDeSesion), new DirectorioConTraza(Roles, Eventos), Catalogo, Repositorio, Transaccion, Bloqueo,
                    NullLogger<AceptarPropuestaApoyoCarteraCommandHandler>.Instance)
                .Handle(new AceptarPropuestaApoyoCarteraCommand(propuestaId), default);

        public Task<Result> Rechazar(Guid actor, Guid propuestaId, Guid? origen = null) =>
            new RechazarPropuestaApoyoCarteraCommandHandler(Como(actor, origen), Roles, Catalogo, Repositorio)
                .Handle(new RechazarPropuestaApoyoCarteraCommand(propuestaId), default);

        public Task<Result> Retirar(Guid actor, Guid propuestaId, Guid? origen = null) =>
            new RetirarPropuestaApoyoCarteraCommandHandler(Como(actor, origen), Roles, Catalogo, Repositorio)
                .Handle(new RetirarPropuestaApoyoCarteraCommand(propuestaId), default);

        public Task<PropuestasApoyoPendientesDto> Pendientes(Guid actor, Guid? origen = null, string? rolDeSesion = "GestorCae") =>
            new ObtenerPropuestasApoyoPendientesQueryHandler(Como(actor, origen, rolDeSesion), Roles, Repositorio, Tenants)
                .Handle(new ObtenerPropuestasApoyoPendientesQuery(), default);

        public Task<IReadOnlyList<DestinatarioDeApoyoDto>> Destinatarios(Guid actor, Guid? operacion = null) =>
            new ObtenerDestinatariosDeApoyoQueryHandler(Como(actor), Roles, GestoresCae, Catalogo, Repositorio)
                .Handle(new ObtenerDestinatariosDeApoyoQuery(operacion ?? Operacion.Id), default);

        /// <summary>Una propuesta pendiente de <see cref="GestorA"/> a <see cref="GestorB"/>, ya guardada.</summary>
        public PropuestaApoyoCartera PropuestaPendiente(Guid? proponente = null, Guid? destinatario = null)
        {
            var propuesta = PropuestaApoyoCartera.Crear(
                Operacion, proponente ?? GestorA, destinatario ?? GestorB, null, DateTime.UtcNow);
            Repositorio.Agregar(propuesta);
            return propuesta;
        }

        public void NoSeEmitioNada()
        {
            Catalogo.CarterasVivas.Select(c => c.Cartera.UsuarioId).Should().BeEquivalentTo([GestorA, GestorC]);
            Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        }
    }

    // ================= Proponer =================

    [Fact]
    public async Task El_Gestor_CAE_principal_propone_y_la_propuesta_queda_pendiente_sin_conceder_nada()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.EsExitoso.Should().BeTrue();
        var propuesta = e.Repositorio.Propuestas.Should().ContainSingle().Subject;
        propuesta.Id.Should().Be(resultado.Valor);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        propuesta.OperadorTenantId.Should().Be(Operador);
        propuesta.PropietarioTenantId.Should().Be(e.Empresa.Id);
        propuesta.AsignacionOperacionId.Should().Be(e.Operacion.Id);
        propuesta.ProponenteUsuarioId.Should().Be(GestorA);
        propuesta.DestinatarioUsuarioId.Should().Be(GestorB);
        propuesta.VigenciaHastaPropuesta.Should().BeNull("la fecha de fin del apoyo todavía no se ofrece");
        e.Catalogo.TenantsAlGuardar.Should().Equal([Operador], "la propuesta es del Operador CAE: se guarda en el Tenant de origen");
        e.Catalogo.ApoyosPedidos.Should().BeEmpty("proponer no emite ninguna cartera");
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task El_Coordinador_CAE_principal_tambien_propone()
    {
        var e = new Escenario();
        e.Cartera(Coordinador, rol: "CoordinadorCae");
        e.PasarLaMarcaA(Coordinador);

        var resultado = await e.Proponer(Coordinador, GestorB, rolDeSesion: "CoordinadorCae");

        resultado.EsExitoso.Should().BeTrue();
        e.Repositorio.Propuestas.Should().ContainSingle().Which.ProponenteUsuarioId.Should().Be(Coordinador);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_apoyo_no_propone()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(GestorC, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEresElPrincipal);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Coordinador_CAE_sin_la_marca_no_propone_aunque_el_principal_sea_de_su_equipo()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(Coordinador, GestorB, rolDeSesion: "CoordinadorCae");

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEresElPrincipal);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    public async Task Otros_roles_del_Operador_CAE_no_proponen(string rol)
    {
        var e = new Escenario();
        var actor = Guid.NewGuid();
        e.Roles.Asignar(actor, Operador, rol);
        e.Cartera(actor);
        e.PasarLaMarcaA(actor);

        var resultado = await e.Proponer(actor, GestorB, rolDeSesion: rol);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_de_quien_propone_sale_de_Identity_y_no_del_claim()
    {
        // Dentro de un Workspace operativo derivado el claim es el de la cartera en ese Tenant
        // propietario. Ni un claim «menor» impide proponer, ni uno de Gestor CAE lo permite a
        // quien ya no lo es en su Tenant de origen.
        var conClaimAjeno = new Escenario();
        var sinRolEnOrigen = new Escenario();
        sinRolEnOrigen.Roles.Asignar(GestorA, Operador, "Consulta");

        var permitido = await conClaimAjeno.Proponer(GestorA, GestorB, rolDeSesion: "Consulta");
        var denegado = await sinRolEnOrigen.Proponer(GestorA, GestorB, rolDeSesion: "GestorCae");

        permitido.EsExitoso.Should().BeTrue();
        denegado.Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        sinRolEnOrigen.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_sesion_sin_rol_de_negocio_no_propone()
    {
        // Sesión Privilegiada de Soporte TALVEG: nunca es Gestor CAE ni Operador CAE.
        var e = new Escenario();

        var resultado = await e.Proponer(GestorA, GestorB, rolDeSesion: null);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_propone_aunque_su_sesion_siga_viva()
    {
        var e = new Escenario();
        e.Roles.Desactivar(GestorA);

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
    }

    [Fact]
    public async Task No_se_propone_a_un_Gestor_CAE_de_otro_Operador_CAE()
    {
        var e = new Escenario();
        // Aunque el catálogo lo diera por candidato: la cuenta no es de este Operador CAE.
        e.Catalogo.RegistrarCandidato(Operador, Ajeno, e.Operacion, e.Empresa.Nombre);

        var resultado = await e.Proponer(GestorA, Ajeno);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioNoValido);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task No_se_propone_a_quien_no_es_Gestor_CAE()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(GestorA, Coordinador);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioNoValido);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task No_se_propone_a_una_cuenta_desactivada()
    {
        var e = new Escenario();
        e.Roles.Desactivar(GestorB);

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioNoValido);
    }

    [Fact]
    public async Task Nadie_se_propone_un_apoyo_a_si_mismo()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(GestorA, GestorA);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioNoValido);
    }

    [Fact]
    public async Task No_se_propone_a_quien_ya_tiene_el_Tenant_en_su_cartera()
    {
        var e = new Escenario();

        var resultado = await e.Proponer(GestorA, GestorC);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioYaEnCartera);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task No_se_propone_dos_veces_a_la_misma_persona_mientras_la_primera_sigue_pendiente()
    {
        var e = new Escenario();
        e.PropuestaPendiente();

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.YaPendiente);
        e.Repositorio.Propuestas.Should().ContainSingle();
    }

    [Fact]
    public async Task Dos_propuestas_a_la_vez_la_que_pierde_el_indice_unico_recibe_ya_pendiente()
    {
        var e = new Escenario();
        e.Catalogo.PierdeLaCarrera = true;

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.YaPendiente);
    }

    [Fact]
    public async Task No_se_propone_sobre_una_operacion_que_ya_no_se_puede_poner_en_una_cartera()
    {
        // Operación caducada, o delegación retirada por el Tenant propietario: deja de ser asignable.
        var e = new Escenario();
        e.Catalogo.Asignables.Clear();

        var resultado = await e.Proponer(GestorA, GestorB);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.OperacionNoDisponible);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task No_se_propone_sobre_la_operacion_de_otro_Operador_CAE()
    {
        // El principal lo es en su Operador CAE; visto desde otro, esa operación no existe.
        var e = new Escenario();

        var resultado = await e.Proponer(Ajeno, GestorB, origen: OtroOperador);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEresElPrincipal);
        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public void Ni_el_rol_ni_el_ambito_ni_la_marca_son_parametros_de_los_comandos()
    {
        typeof(ProponerApoyoCarteraCommand).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["AsignacionOperacionId", "DestinatarioUsuarioId"],
                "quien propone elige la empresa y la persona; el rol, el ámbito y la marca los fija el catálogo");
        typeof(AceptarPropuestaApoyoCarteraCommand).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["PropuestaId"]);
    }

    // ================= Aceptar =================

    [Fact]
    public async Task El_destinatario_acepta_y_recibe_una_cartera_de_apoyo_que_no_es_principal()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
        propuesta.AsignacionCarteraId.Should().NotBeNull();
        propuesta.AsignacionOperadorDelegadoId.Should().NotBeNull();

        e.Catalogo.ApoyosPedidos.Should().ContainSingle()
            .Which.TenantActivo.Should().Be(e.Empresa.Id, "la cartera se escribe con el Tenant propietario como Tenant activo");
        e.Catalogo.TenantsAlGuardar.Should().Equal([e.Empresa.Id], "cartera y aceptación van en un solo guardado");
        e.Catalogo.IncorporacionesDirectas.Should().BeEmpty("la vía de apoyo no pasa por la emisión que puede marcar principal");

        var emitida = e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == GestorB).Cartera;
        emitida.EsPrincipal.Should().BeFalse();
        emitida.Rol.Should().Be("GestorCae");
        e.Principal.Should().Be(GestorA, "aceptar un apoyo no mueve la marca");
        e.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Antes_de_leer_ninguna_cuenta_toma_el_candado_compartido_sobre_el_destinatario_y_sobre_quien_propuso()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        await e.Aceptar(GestorB, propuesta.Id);

        e.Bloqueo.Compartidos.Should().BeEquivalentTo([GestorB, GestorA]);
        e.Bloqueo.Exclusivos.Should().BeEmpty();
        // Las lecturas anteriores al candado son las de la sesión de quien acepta (fuera de la
        // transacción, solo para fallar pronto). Dentro, las dos cuentas se leen después.
        var trasElCandado = e.Eventos.SkipWhile(ev => ev != "compartido").Skip(1).ToList();
        trasElCandado.Should().Contain($"cuenta:{GestorB}").And.Contain($"cuenta:{GestorA}");
        e.Eventos.TakeWhile(ev => ev != "compartido").Should().NotContain($"cuenta:{GestorA}",
            "la cuenta de quien propuso no se lee antes de protegerla");
        e.Transaccion.Ejecutadas.Should().Be(1, "el candado solo vale dentro de la transacción");
    }

    [Fact]
    public async Task Quien_propuso_no_puede_aceptar_por_el_destinatario()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Aceptar(GestorA, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        e.NoSeEmitioNada();
    }

    [Theory]
    [InlineData("otro Gestor CAE")]
    [InlineData("Coordinador CAE")]
    public async Task Un_tercero_del_mismo_Operador_CAE_no_puede_aceptar(string quien)
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        var (actor, rol) = quien == "Coordinador CAE" ? (Coordinador, "CoordinadorCae") : (GestorD, "GestorCae");

        var resultado = await e.Aceptar(actor, propuesta.Id, rolDeSesion: rol);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Para_otro_Operador_CAE_la_propuesta_no_existe()
    {
        // Ni siquiera si el Id del destinatario coincidiera: la propuesta se carga por Operador CAE.
        var e = new Escenario();
        e.Roles.Asignar(GestorB, OtroOperador, "GestorCae");
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Aceptar(GestorB, propuesta.Id, origen: OtroOperador);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Si_quien_propuso_ya_no_lleva_la_marca_la_propuesta_se_anula_y_la_anulacion_se_guarda()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.PasarLaMarcaA(GestorC);

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.AnuladaProponenteYaNoEsPrincipal);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        propuesta.MotivoAnulacion.Should().Be(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal);
        propuesta.AsignacionCarteraId.Should().BeNull();
        e.NoSeEmitioNada();
        e.Transaccion.Confirmadas.Should().Be(1, "si la transacción se deshiciera, la propuesta seguiría pendiente para siempre");
        e.Transaccion.Deshechas.Should().Be(0);
    }

    [Theory]
    [InlineData("desactivada")]
    [InlineData("sin rol de gestión CAE")]
    public async Task Si_la_cuenta_de_quien_propuso_ya_no_es_de_gestion_CAE_activa_la_propuesta_se_anula(string caso)
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        if (caso == "desactivada") e.Roles.Desactivar(GestorA);
        else e.Roles.Asignar(GestorA, Operador, "Consulta");

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.AnuladaProponenteYaNoEsPrincipal);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        e.Catalogo.ApoyosPedidos.Should().BeEmpty("no se llega a pedir la emisión");
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Si_el_destinatario_ya_no_es_Gestor_CAE_la_propuesta_se_anula()
    {
        // Lo ascendieron a Coordinador CAE mientras la propuesta esperaba: un Coordinador CAE
        // no recibe carteras de apoyo de Gestor CAE por esta vía.
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Roles.Asignar(GestorB, Operador, "CoordinadorCae");

        var resultado = await e.Aceptar(GestorB, propuesta.Id, rolDeSesion: "CoordinadorCae");

        resultado.Error.Should().Be(ErroresPropuestaApoyo.AnuladaDestinatarioNoDisponible);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        propuesta.MotivoAnulacion.Should().Be(MotivoAnulacionPropuestaApoyo.DestinatarioNoDisponible);
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Un_destinatario_desactivado_no_acepta_aunque_su_sesion_siga_viva()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Roles.Desactivar(GestorB);

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        e.NoSeEmitioNada();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Theory]
    [InlineData(MotivoAnulacionPropuestaApoyo.YaEnCartera, "PropuestaApoyo.AnuladaYaEnCartera")]
    [InlineData(MotivoAnulacionPropuestaApoyo.OperacionNoVigente, "PropuestaApoyo.AnuladaOperacionNoVigente")]
    [InlineData(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal, "PropuestaApoyo.AnuladaProponenteYaNoEsPrincipal")]
    public async Task Si_el_catalogo_ya_no_puede_emitir_la_propuesta_se_anula_con_su_motivo(
        MotivoAnulacionPropuestaApoyo motivo, string codigo)
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Catalogo.AnularAlIncorporarApoyo = motivo;

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Codigo.Should().Be(codigo);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        propuesta.MotivoAnulacion.Should().Be(motivo);
        e.NoSeEmitioNada();
        e.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Si_el_guardado_pierde_una_carrera_no_queda_nada_escrito()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Catalogo.PierdeLaCarrera = true;

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.CambioMientrasDecidias);
        e.Transaccion.Deshechas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    /// <summary>
    /// A quien acepta se le dice que algo cambió, pero la excepción no se pierde: un fallo
    /// determinista (clave ajena, tipo) llega por el mismo <c>catch</c> que una carrera, y sin
    /// la entrada de registro —con el id de la propuesta y la excepción— no se distinguirían.
    /// </summary>
    [Fact]
    public async Task Un_fallo_de_base_de_datos_al_confirmar_se_devuelve_como_cambio_concurrente_y_queda_registrado()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        var registro = new RegistroCaptura();
        var handler = new AceptarPropuestaApoyoCarteraCommandHandler(
            e.Como(GestorB), e.Roles, e.Catalogo, e.Repositorio, new TransaccionQueFalla(), e.Bloqueo, registro);

        var resultado = await handler.Handle(new AceptarPropuestaApoyoCarteraCommand(propuesta.Id), default);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.CambioMientrasDecidias);
        var entrada = registro.Entradas.Should().ContainSingle().Subject;
        entrada.Nivel.Should().Be(LogLevel.Warning);
        entrada.Mensaje.Should().Contain(propuesta.Id.ToString());
        entrada.Excepcion.Should().BeOfType<DbUpdateException>()
            .Which.Message.Should().Be("conflicto al confirmar");
    }

    /// <summary>Control del anterior: una aceptación sin fallo no escribe nada en el registro.</summary>
    [Fact]
    public async Task Una_aceptacion_sin_fallo_de_base_de_datos_no_registra_nada()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        var registro = new RegistroCaptura();
        var handler = new AceptarPropuestaApoyoCarteraCommandHandler(
            e.Como(GestorB), e.Roles, e.Catalogo, e.Repositorio, e.Transaccion, e.Bloqueo, registro);

        var resultado = await handler.Handle(new AceptarPropuestaApoyoCarteraCommand(propuesta.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        registro.Entradas.Should().BeEmpty();
    }

    private sealed class TransaccionQueFalla : ITransaccionDeComando
    {
        public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("conflicto al confirmar");
    }

    private sealed class RegistroCaptura : ILogger<AceptarPropuestaApoyoCarteraCommandHandler>
    {
        public List<(LogLevel Nivel, string Mensaje, Exception? Excepcion)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception), exception));
    }

    [Theory]
    [InlineData("Aceptada")]
    [InlineData("Rechazada")]
    [InlineData("Retirada")]
    public async Task Una_propuesta_que_ya_no_esta_pendiente_no_se_acepta(string estado)
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        switch (estado)
        {
            case "Aceptada": (await e.Aceptar(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue(); break;
            case "Rechazada": propuesta.Rechazar(GestorB, DateTime.UtcNow); break;
            default: propuesta.Retirar(GestorA, DateTime.UtcNow); break;
        }

        var carterasAntes = e.Catalogo.CarterasVivas.Count;

        var resultado = await e.Aceptar(GestorB, propuesta.Id);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.YaResuelta);
        e.Catalogo.CarterasVivas.Should().HaveCount(carterasAntes);
    }

    [Fact]
    public async Task Se_acepta_con_otro_Tenant_activo_y_el_claim_de_esa_cartera()
    {
        // El destinatario trabaja dentro de otro Tenant propietario: su claim de rol es el de
        // su cartera allí. La propuesta es de su Operador CAE y se resuelve por el Tenant de origen.
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Aceptar(GestorB, propuesta.Id, rolDeSesion: "Consulta");

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
    }

    // ================= Rechazar y retirar =================

    [Fact]
    public async Task El_destinatario_rechaza_y_no_se_emite_nada()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Rechazar(GestorB, propuesta.Id);

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Rechazada);
        e.Catalogo.TenantsAlGuardar.Should().Equal([Operador]);
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Solo_el_destinatario_rechaza()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Roles.Asignar(GestorB, OtroOperador, "GestorCae");

        (await e.Rechazar(GestorA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada, "quien propuso retira, no rechaza");
        (await e.Rechazar(GestorD, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        (await e.Rechazar(GestorB, propuesta.Id, origen: OtroOperador)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public async Task Quien_propuso_retira_la_propuesta_pendiente()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var resultado = await e.Retirar(GestorA, propuesta.Id);

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Retirada);
        e.Catalogo.TenantsAlGuardar.Should().Equal([Operador]);
        e.NoSeEmitioNada();
    }

    [Fact]
    public async Task Solo_quien_propuso_retira()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Roles.Asignar(GestorA, OtroOperador, "GestorCae");

        (await e.Retirar(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada, "el destinatario rechaza, no retira");
        (await e.Retirar(GestorD, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        (await e.Retirar(GestorA, propuesta.Id, origen: OtroOperador)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    [Fact]
    public async Task Rechazar_o_retirar_lo_que_ya_no_esta_pendiente_o_perder_la_carrera_devuelve_ya_resuelta()
    {
        var e = new Escenario();
        var rechazada = e.PropuestaPendiente();
        rechazada.Rechazar(GestorB, DateTime.UtcNow);
        var enCarrera = e.PropuestaPendiente(destinatario: GestorD);

        (await e.Rechazar(GestorB, rechazada.Id)).Error.Should().Be(ErroresPropuestaApoyo.YaResuelta);
        (await e.Retirar(GestorA, rechazada.Id)).Error.Should().Be(ErroresPropuestaApoyo.YaResuelta);

        e.Catalogo.PierdeLaCarrera = true;
        (await e.Rechazar(GestorD, enCarrera.Id)).Error.Should().Be(ErroresPropuestaApoyo.YaResuelta);
    }

    [Fact]
    public async Task Sin_rol_de_gestion_CAE_en_origen_no_se_rechaza_ni_se_retira()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        e.Roles.Desactivar(GestorA);
        e.Roles.Desactivar(GestorB);

        (await e.Rechazar(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        (await e.Retirar(GestorA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
    }

    // ================= Consultas =================

    [Fact]
    public async Task Cada_uno_ve_las_que_tiene_que_responder_y_las_que_propuso()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();

        var delDestinatario = await e.Pendientes(GestorB);
        var delProponente = await e.Pendientes(GestorA);
        var deUnTercero = await e.Pendientes(GestorD);

        var recibida = delDestinatario.Recibidas.Should().ContainSingle().Subject;
        recibida.Id.Should().Be(propuesta.Id);
        recibida.TenantPropietarioId.Should().Be(e.Empresa.Id);
        recibida.NombreEmpresa.Should().Be(e.Empresa.Nombre);
        recibida.ProponenteUsuarioId.Should().Be(GestorA);
        recibida.NombreProponente.Should().NotBeNullOrWhiteSpace();
        delDestinatario.Enviadas.Should().BeEmpty();

        delProponente.Enviadas.Should().ContainSingle().Which.DestinatarioUsuarioId.Should().Be(GestorB);
        delProponente.Recibidas.Should().BeEmpty();

        deUnTercero.Should().Be(PropuestasApoyoPendientesDto.Vacia);
    }

    [Fact]
    public async Task Las_resueltas_dejan_de_salir()
    {
        var e = new Escenario();
        var propuesta = e.PropuestaPendiente();
        propuesta.Rechazar(GestorB, DateTime.UtcNow);

        (await e.Pendientes(GestorB)).Recibidas.Should().BeEmpty();
        (await e.Pendientes(GestorA)).Enviadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Desde_otro_Operador_CAE_o_sin_rol_de_gestion_CAE_la_lista_sale_vacia_sin_error()
    {
        var e = new Escenario();
        e.PropuestaPendiente();
        e.Roles.Asignar(GestorB, OtroOperador, "GestorCae");
        var administrador = Guid.NewGuid();
        e.Roles.Asignar(administrador, Operador, "Administrador");

        (await e.Pendientes(GestorB, origen: OtroOperador)).Should().Be(PropuestasApoyoPendientesDto.Vacia);
        (await e.Pendientes(administrador, rolDeSesion: "Administrador")).Should().Be(PropuestasApoyoPendientesDto.Vacia);
        (await e.Pendientes(GestorB, rolDeSesion: null)).Should().Be(PropuestasApoyoPendientesDto.Vacia);
    }

    [Fact]
    public async Task El_principal_elige_entre_los_Gestores_CAE_de_su_Operador_CAE_sin_cartera_ni_propuesta_pendiente()
    {
        var e = new Escenario();
        e.PropuestaPendiente(destinatario: GestorD);

        var destinatarios = await e.Destinatarios(GestorA);

        destinatarios.Select(d => d.UsuarioId).Should().Equal([GestorB],
            "ni él mismo, ni quien ya tiene cartera, ni quien ya tiene su propuesta pendiente, ni el Gestor CAE de otro Operador CAE");
    }

    [Fact]
    public async Task Quien_no_lleva_la_marca_no_recibe_a_quien_proponer()
    {
        var e = new Escenario();

        (await e.Destinatarios(GestorC)).Should().BeEmpty();
        (await e.Destinatarios(GestorA, operacion: Guid.NewGuid())).Should().BeEmpty();
    }
}

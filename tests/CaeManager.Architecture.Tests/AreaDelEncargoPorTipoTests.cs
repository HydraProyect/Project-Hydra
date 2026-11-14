using System.Runtime.CompilerServices;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests
{
    /// <summary>
    /// <b>El área de <see cref="ActosExcluidosDelEncargo"/> se resuelve por tipo, no por la carpeta</b> (ADR-013, paso
    /// M0c). Antes, el área era el primer segmento del espacio de nombres tras <c>CaeManager.Application.</c>: mover
    /// <c>AutorizacionesLectura</c> a <c>CaeManager.Application.Inbound.*</c> (paso M1) le habría dado el área
    /// «Inbound», que no está excluida, y la habría abierto a quien administra por Encargo de administración no pleno.
    ///
    /// <para>
    /// <b>Lo que observa</b>: por reflexión, toda petición concreta del ensamblado de Application, su
    /// <see cref="AreaDelEncargoAttribute"/>, el módulo que le da el registro de M0a (<see cref="RegistroDeModulos"/>,
    /// solo desde tests) y el resultado de <see cref="ActosExcluidosDelEncargo.Excluye"/> sobre una instancia sin
    /// inicializar. <b>Lo que NO observa</b>: si la clasificación de un área es acertada —eso es una decisión (D-8)—,
    /// ni el comportamiento del behavior en ejecución, que fijan los tests de Application.
    /// </para>
    /// </summary>
    public class AreaDelEncargoPorTipoTests
    {
        private static readonly List<Type> Peticiones = typeof(ActosExcluidosDelEncargo).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IBaseRequest).IsAssignableFrom(t))
            .ToList();

        /// <summary>
        /// El conjunto de peticiones que <see cref="ActosExcluidosDelEncargo.Excluye"/> deja fuera del encargo no pleno,
        /// congelado del resultado de <c>main</c> en 05e5e1a0d (antes de M0c). Va por nombre sin espacio de nombres para
        /// que sobreviva a M1: mover una petición no debe tocar esta lista. <c>ObtenerAuditoriaQuery</c> entra
        /// porque sin inicializar lleva <c>EntidadId</c> nulo, su variante excluida.
        /// </summary>
        private static readonly string[] ExcluidasCongeladas =
        [
            "ActualizarDeteccionTrabajadoresGlobalCommand",
            "ActualizarLecturaIaGlobalCommand",
            "ActualizarLineaWhatsAppCommand",
            "ActualizarPerfilDocumentoOficialGlobalCommand",
            "ActualizarPresupuestoIaCommand",
            "ActualizarSeguridadAccesoCommand",
            "ActualizarTarifaClienteCommand",
            "ActualizarVerificacionIaGlobalCommand",
            "AnalizarImportacionExcelQuery",
            "AnalizarPlantillaClientesQuery",
            "AnalizarPlantillaCombinadaQuery",
            "AnalizarPlantillaDocumentosQuery",
            "AnalizarPlantillaMaestraQuery",
            "AsignarTrabajadoresAutorizacionCommand",
            "BuscarDatosPurgablesCommand",
            "CancelarPurgaCommand",
            "ConcederAutorizacionLecturaCommand",
            "ConectarBuzonMicrosoft365Command",
            "ConfirmarYEnviarPrimerAvisoCommand",
            "CrearLineaWhatsAppCommand",
            "CrearTarifaClienteCommand",
            "DecidirIncidenteSeguridadCommand",
            "DesconectarBuzonCommand",
            "DiagnosticarDatosPurgablesCommand",
            "EjecutarImportacionCombinadaCommand",
            "EjecutarImportacionCommand",
            "EjecutarImportacionMaestraCommand",
            "EjecutarPurgaCommand",
            "EliminarTarifaClienteCommand",
            "EnviarNotificacionCompletaCommand",
            "FijarAcuseAutomaticoBuzonCommand",
            "FijarResponsableBuzonCommand",
            "GenerarClaveApiCommand",
            "GuardarConexionPlataformaCaeCommand",
            "MarcarCobroLiquidacionOperadorCaeCommand",
            "MarcarTenantAvisadoCommand",
            "ObtenerAccesosDocumentosSensiblesQuery",
            "ObtenerAuditoriaIaQuery",
            "ObtenerAuditoriaQuery",
            "ObtenerAutorizacionesLecturaQuery",
            "ObtenerClavesApiDeMiOrganizacionQuery",
            "ObtenerClavesApiQuery",
            "ObtenerCobrosLiquidacionOperadorCaeQuery",
            "ObtenerContactoSeguridadDeMiOrganizacionQuery",
            "ObtenerContactoSeguridadVigenteQuery",
            "ObtenerContactosSeguridadPropuestosQuery",
            "ObtenerHistorialImportacionesQuery",
            "ObtenerIncidenteSeguridadQuery",
            "ObtenerLineasWhatsAppQuery",
            "ObtenerRegistroAuditoriaPorIdQuery",
            "ObtenerResumenFacturacionQuery",
            "ObtenerSolicitudesPurgaQuery",
            "ObtenerTarifasClienteQuery",
            "PrevisualizarImportacionMaestraCommand",
            "ProgramarPurgaCommand",
            "ProponerContactoSeguridadCommand",
            "ReactivarConexionCommand",
            "RechazarContactoSeguridadCommand",
            "RegistrarHistorialImportacionCommand",
            "RepararMarcaHuecoCommand",
            "ResolverDestinatariosAvisoSeguridadQuery",
            "RetirarConexionPlataformaCaeCommand",
            "RetirarTrabajadorAutorizacionCommand",
            "RevocarAutorizacionLecturaCommand",
            "RevocarClaveApiCommand",
            "RevocarClaveApiDeMiOrganizacionCommand",
            "ValidarContactoSeguridadCommand",
        ];

        private static string NombreCorto(Type t) => t.FullName![(t.Namespace!.Length + 1)..];

        private static bool ExcluyeSinInicializar(Type t) =>
            ActosExcluidosDelEncargo.Excluye(RuntimeHelpers.GetUninitializedObject(t));

        [Fact]
        public void El_conjunto_excluido_es_identico_al_congelado_de_main()
        {
            Peticiones.Should().HaveCountGreaterThan(300, "control positivo: el detector ve las peticiones de Application");
            Peticiones.Select(NombreCorto).Should().OnlyHaveUniqueItems(
                "la lista congelada identifica cada petición por su nombre sin espacio de nombres");

            Peticiones.Where(ExcluyeSinInicializar).Select(NombreCorto).Should().BeEquivalentTo(ExcluidasCongeladas,
                "M0c cambia cómo se resuelve el área, no qué queda excluido; una petición nueva excluida o una que deja "
                + "de estarlo es una decisión (D-8) que se escribe aquí en el mismo commit");
        }

        [Fact]
        public void Cada_peticion_Inbound_lleva_su_area_en_el_tipo_y_su_exclusion_queda_fijada()
        {
            var registro = RegistroDeModulos.Versionado();
            var inbound = Peticiones.Where(p => registro.Clasificar(p)?.Modulo == Modulo.Inbound).ToList();
            inbound.Should().HaveCountGreaterThan(20,
                "control positivo: ContratosAcceso, AutorizacionesLectura y CatalogoBase tienen peticiones Inbound");

            inbound.Should().OnlyContain(p => Marca(p) != null,
                "una petición Inbound declara su área en el tipo: M1 la moverá a CaeManager.Application.Inbound.*");
            inbound.Where(ExcluyeSinInicializar).Select(NombreCorto).Should().BeEquivalentTo(
                inbound.Select(NombreCorto).Intersect(ExcluidasCongeladas),
                "la exclusión de cada petición Inbound es la congelada");
            inbound.Where(p => Marca(p) == "AutorizacionesLectura").Should().NotBeEmpty()
                .And.OnlyContain(p => ExcluyeSinInicializar(p),
                    "las autorizaciones de lectura están excluidas enteras del encargo no pleno");
        }

        /// <summary>
        /// Llevan la marca las peticiones de un área excluida entera (lo que más cuesta abrir por accidente) y las de los
        /// módulos Inbound y Facturación (lo que ADR-013 prevé mover). Mientras una petición no se haya movido, su marca
        /// coincide con su carpeta; cuando el espacio de nombres deje de ser un área (por ejemplo «Inbound»), manda la
        /// marca. Si una petición marcada acaba en la carpeta de otra área clasificada, este test se pone en rojo: es una
        /// reclasificación y se decide, no se hereda de la carpeta.
        /// </summary>
        [Fact]
        public void Las_peticiones_que_deben_llevar_la_marca_la_llevan_y_coincide_con_su_carpeta()
        {
            var registro = RegistroDeModulos.Versionado();
            var obligadas = Peticiones
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) is { } a && ActosExcluidosDelEncargo.AreasExcluidas.Contains(a)
                            || registro.Clasificar(p)?.Modulo is Modulo.Inbound or Modulo.Facturacion)
                .ToList();
            obligadas.Should().HaveCountGreaterThan(60, "control positivo: siete áreas excluidas y los módulos Inbound y Facturación");

            obligadas.Where(p => Marca(p) is null).Select(p => p.FullName).Should().BeEmpty(
                "estas peticiones declaran su área con [AreaDelEncargo(\"…\")] en el tipo");

            var marcadas = Peticiones.Where(p => Marca(p) != null).ToList();
            marcadas.Should().OnlyContain(p => ActosExcluidosDelEncargo.AreasExcluidas.Contains(Marca(p)!)
                                               || ActosExcluidosDelEncargo.AreasPermitidas.Contains(Marca(p)!),
                "la marca nombra un área clasificada; una que no lo está deja la petición sin área y excluida");
            marcadas
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) is { } a
                            && (ActosExcluidosDelEncargo.AreasExcluidas.Contains(a) || ActosExcluidosDelEncargo.AreasPermitidas.Contains(a)))
                .Where(p => ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(p) != Marca(p))
                .Select(p => p.FullName).Should().BeEmpty(
                    "una petición en la carpeta de un área clasificada lleva esa misma área en la marca");
            marcadas.Should().OnlyContain(p => ActosExcluidosDelEncargo.AreaDe(p) == Marca(p), "la marca manda sobre la carpeta");
        }

        /// <summary>
        /// Movimiento simulado: la misma petición, con la misma marca, antes y después de M1. Con la regla anterior la
        /// excluida movida habría quedado abierta (área «Inbound»); con la de M0c da lo mismo en los dos sitios.
        /// </summary>
        [Fact]
        public void Mover_una_peticion_de_carpeta_no_cambia_si_queda_excluida()
        {
            var excluidaAntes = typeof(CaeManager.Application.AutorizacionesLectura.SimulacionM0c.PeticionExcluidaSimulada);
            var excluidaDespues = typeof(CaeManager.Application.Inbound.AutorizacionesLectura.SimulacionM0c.PeticionExcluidaSimulada);
            var permitidaAntes = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionPermitidaSimulada);
            var permitidaDespues = typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.PeticionPermitidaSimulada);

            // Control de sensibilidad: con la regla anterior (el espacio de nombres) la movida tendría área «Inbound»,
            // que no está excluida.
            ActosExcluidosDelEncargo.AreaPorEspacioDeNombres(excluidaDespues).Should().Be("Inbound");
            ActosExcluidosDelEncargo.AreasExcluidas.Should().NotContain("Inbound");

            ActosExcluidosDelEncargo.AreaDe(excluidaAntes).Should().Be("AutorizacionesLectura");
            ActosExcluidosDelEncargo.AreaDe(excluidaDespues).Should().Be("AutorizacionesLectura");
            ExcluyeSinInicializar(excluidaAntes).Should().BeTrue();
            ExcluyeSinInicializar(excluidaDespues).Should().BeTrue("moverla no la abre");

            ActosExcluidosDelEncargo.AreaDe(permitidaAntes).Should().Be("ContratosAcceso");
            ActosExcluidosDelEncargo.AreaDe(permitidaDespues).Should().Be("ContratosAcceso");
            ExcluyeSinInicializar(permitidaAntes).Should().BeFalse();
            ExcluyeSinInicializar(permitidaDespues).Should().BeFalse("moverla no la cierra");
        }

        /// <summary>
        /// Falla cerrado: una petición sin área resuelta queda excluida, nunca abierta en silencio. Además
        /// <c>AreasClasificadasParaElEncargoTests</c> pone en rojo el commit que la crea en Application.
        /// </summary>
        [Fact]
        public void Una_peticion_sin_area_resuelta_queda_excluida()
        {
            var sinMarcaAntes = typeof(CaeManager.Application.ContratosAcceso.SimulacionM0c.PeticionSinMarcaSimulada);
            var sinMarcaMovida = typeof(CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c.PeticionSinMarcaSimulada);

            ActosExcluidosDelEncargo.AreaDe(sinMarcaAntes).Should().Be("ContratosAcceso", "sin marca, el espacio de nombres");
            ExcluyeSinInicializar(sinMarcaAntes).Should().BeFalse();

            ActosExcluidosDelEncargo.AreaDe(sinMarcaMovida).Should().BeNull("«Inbound» es un módulo, no un área");
            ExcluyeSinInicializar(sinMarcaMovida).Should().BeTrue("sin área, falla cerrado");

            ActosExcluidosDelEncargo.AreaDe(typeof(PeticionConAreaInexistente)).Should().BeNull();
            ExcluyeSinInicializar(typeof(PeticionConAreaInexistente)).Should().BeTrue("una marca con un área no clasificada no abre nada");

            ActosExcluidosDelEncargo.AreaDe(typeof(PeticionFueraDeApplication)).Should().BeNull();
            ExcluyeSinInicializar(typeof(PeticionFueraDeApplication)).Should().BeTrue("fuera de la raíz y sin marca no hay área");
        }

        [Fact]
        public void Ningun_segmento_de_modulo_se_clasifica_como_area()
        {
            // «Facturacion» es a la vez módulo (D-M1) y área excluida entera: lo excluido no abre nada.
            var clasificadas = ActosExcluidosDelEncargo.AreasPermitidas.Concat(ActosExcluidosDelEncargo.AreasExcluidas).ToList();
            clasificadas.Should().NotContain(["Inbound", "Outbound", "Nucleo", "Composicion"],
                "clasificar un módulo como área abriría de golpe lo que se mueva a él (ADR-013 § 5, corrección A-3)");
        }

        private static string? Marca(Type t) =>
            t.GetCustomAttributes(typeof(AreaDelEncargoAttribute), inherit: false).Cast<AreaDelEncargoAttribute>().SingleOrDefault()?.Area;

        [AreaDelEncargo("NoEsUnArea")]
        private sealed record PeticionConAreaInexistente : IRequest;

        private sealed record PeticionFueraDeApplication : IRequest;
    }
}

// Tipos sintéticos del movimiento simulado: la misma petición antes (carpeta de su área) y después de M1
// (CaeManager.Application.Inbound.*). Viven en el ensamblado de tests; los registros de Application y de módulos no los ven.
namespace CaeManager.Application.AutorizacionesLectura.SimulacionM0c
{
    [AreaDelEncargo("AutorizacionesLectura")]
    public sealed record PeticionExcluidaSimulada : IRequest;
}

namespace CaeManager.Application.Inbound.AutorizacionesLectura.SimulacionM0c
{
    [AreaDelEncargo("AutorizacionesLectura")]
    public sealed record PeticionExcluidaSimulada : IRequest;
}

namespace CaeManager.Application.ContratosAcceso.SimulacionM0c
{
    [AreaDelEncargo("ContratosAcceso")]
    public sealed record PeticionPermitidaSimulada : IRequest;

    public sealed record PeticionSinMarcaSimulada : IRequest;
}

namespace CaeManager.Application.Inbound.ContratosAcceso.SimulacionM0c
{
    [AreaDelEncargo("ContratosAcceso")]
    public sealed record PeticionPermitidaSimulada : IRequest;

    public sealed record PeticionSinMarcaSimulada : IRequest;
}

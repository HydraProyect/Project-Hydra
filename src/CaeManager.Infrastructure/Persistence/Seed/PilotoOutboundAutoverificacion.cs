using System.IO.Compression;
using System.Security.Claims;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Visitas.GestionPorCorreo;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La autoverificación de la siembra del piloto: mide, con las mismas consultas
/// que pintan las pantallas, los contadores de cada Tenant propietario
/// (<see cref="MedirAsync(IServiceScopeFactory, OpcionesPilotoOutbound, CancellationToken)"/>)
/// y los compara con los valores de la matriz (<see cref="Exigir"/>, pura).
///
/// <para>
/// <b>Con qué identidad mide.</b> Las consultas de pantalla necesitan un usuario:
/// Mi trabajo, Inicio, Centros, Empresas y Clientes empresariales se miden como la
/// Gestora CAE primera del piloto, y Visión de cartera —que la Gestora CAE no
/// puede abrir— como su Coordinadora CAE. La identidad la construye
/// <see cref="ComoCuentaDelPilotoAsync{T}"/>, privado, y solo para esas dos
/// cuentas: comprueba en la base que la cuenta pertenece al Tenant del Operador
/// CAE externo del piloto, que ese Tenant lleva el marcador de datos de demo y
/// que la cuenta tiene exactamente el rol esperado. Vive lo que dura una lectura,
/// fuera de toda petición, y se retira en un <c>finally</c>. No hay ninguna
/// entrada que construya una identidad para otra cuenta: es el único sitio de
/// <c>src/</c> que crea un contexto HTTP a mano, y un trinquete lo fija.
/// </para>
/// </summary>
public static class PilotoOutboundAutoverificacion
{
    public sealed record MedicionCentro(string Nombre, EstadoCentro Estado, int? Cumplimiento);

    /// <summary>Lo medido de un Tenant propietario. Los nombres dicen de qué pantalla sale cada contador.</summary>
    /// <param name="DocumentosConVencimientoAjenoASuTipo">
    /// Documentos de un Tipo que fija la vigencia desde la emisión (<see cref="TipoDocumento.FijaVigenciaDesdeLaEmision"/>)
    /// cuyo vencimiento no es el que el producto calcula: la emisión más los meses del Tipo.
    /// </param>
    /// <param name="DocumentosEmitidosEnElFuturo">Documentos con una emisión posterior a hoy, el día en que se mide.</param>
    public sealed record MedicionTenant(
        string Clave, string Nombre,
        bool MiTrabajoPresente, bool MiTrabajoNoConsultado, bool MiTrabajoAlcanceCero,
        int MiTrabajoBloqueos, int MiTrabajoActuaciones, int MiTrabajoProximos, int MiTrabajoSeguimiento,
        int InicioCumplimiento, int InicioVencidos, int InicioUrgentes, int InicioProximos, int InicioSinConfirmar,
        int InicioCentrosBloqueados, int InicioTrabajadoresBloqueados, int InicioVisitasUrgentes,
        bool VisionCarteraPresente, int? VisionCarteraCumplimiento, bool VisionCarteraSinCartera, bool VisionCarteraSinDatos,
        IReadOnlyList<MedicionCentro> Centros, int? EmpresaCumplimiento,
        int ParesExigidos, int ParesFaltantes,
        int ClientesEmpresariales, int ClientesEmpresarialesSinContacto, int ClientesEmpresarialesConAlertas,
        int Documentos, int DocumentosSinPdf, int DocumentosConVencimientoAjenoASuTipo, int DocumentosEmitidosEnElFuturo,
        int ContactosDeAgenda, int ContactosFueraDeLaReglaDeCorreo,
        MedicionEstructura Estructura)
    {
        public int MiTrabajoFilas => MiTrabajoBloqueos + MiTrabajoActuaciones + MiTrabajoProximos + MiTrabajoSeguimiento;
    }

    /// <summary>
    /// La estructura de un Tenant, medida sobre las filas con la identidad de la Gestora CAE: lo que el perfil declara en el
    /// catálogo (Trabajadores con su ficha, Vehículos con su ficha, Proyectos, Subcontratas, Visitas en cada estado…) y
    /// las invariantes del modelo egocéntrico (una sola Empresa con EsPropia, Centros colgados de un Cliente empresarial del
    /// Tenant, y toda Empresa visible para la Gestora CAE que lo gestiona).
    /// </summary>
    public sealed record MedicionEstructura(
        int TrabajadoresPropios, int TrabajadoresDeSubcontrata, int TrabajadoresSinFicha,
        int ExtranjerosConPermiso, int PermisosCaducados, int PermisosProximosACaducar,
        int Vehiculos, int VehiculosSinFicha, int VehiculosSinConductor,
        int Proyectos, int ProyectosAbiertos,
        int Subcontratas, int SubcontratasSinTrabajadores, int SubcontratasSupervisadas, int VerificacionesExternas,
        int EmpresasPropias, int CentrosFueraDeUnClienteDelTenant, int EmpresasNoVisiblesParaLaGestora,
        int VisitasFuturas, int VisitasRealizadas, int VisitasVencidas, int VisitasCanceladas,
        int VisitasEnCurso, int VisitasConAvisoUrgente,
        int TrabajadoresConAltaPosteriorAUnDocumento, int LibrosDeSubcontratacion, int CertificadosRea, int RecibosDeAutonomos,
        int DocumentosRenovados, int AcreditacionesSubidas, int AcreditacionesRechazadas, int AcreditacionesRechazadasSinMotivo,
        int ReclamacionesSinRespuesta, int ReclamacionesRespondidas);

    /// <summary>
    /// Lo que la comprobación de cartera del Asistente IA (<see cref="ComprobarInstruccionIaCarteraQuery"/>, la que
    /// decide si el texto del usuario sale hacia el proveedor) da a una cuenta: los Tenants de su cartera, por su
    /// nombre, según tengan o no una instrucción de tratamiento de IA vigente. Con uno solo sin ella, el asistente
    /// falla cerrado para esa cuenta.
    /// </summary>
    public sealed record CarteraDelAsistenteIa(IReadOnlyList<string> ConInstruccion, IReadOnlyList<string> SinInstruccion);

    /// <summary>La comprobación de cartera del Asistente IA, medida como la Gestora CAE primera y como la Coordinadora CAE.</summary>
    public sealed record MedicionAsistenteIa(CarteraDelAsistenteIa Gestora, CarteraDelAsistenteIa Coordinadora);

    /// <summary>
    /// Lo que el Tenant del Operador CAE externo enseña en su pantalla de equipo: las cuentas del equipo de la
    /// Coordinadora CAE pendientes de activación. La solicitud de cartera y la propuesta de apoyo (R14) no se siembran: sus
    /// políticas de seguridad de registros exigen el Tenant de origen de una sesión autenticada.
    /// </summary>
    public sealed record MedicionOperador(int PendientesActivacionEquipo);

    /// <param name="AsistenteIa">Nulo si no se midió: <see cref="Repartir"/> lo da por discrepancia, no por bueno.</param>
    /// <param name="Operador">Nulo si no se midió: igual que <paramref name="AsistenteIa"/>.</param>
    /// <param name="DiasHastaLaDemostracion">
    /// D menos el día en que se midió (de 0 a <see cref="OpcionesPilotoOutbound.MargenMaximoDias"/> en una siembra
    /// normal). Nulo si no se conoce: lo que depende del día de medición —las Visitas urgentes— no se compara.
    /// </param>
    public sealed record Informe(
        IReadOnlyList<MedicionTenant> Tenants, MedicionAsistenteIa? AsistenteIa = null, MedicionOperador? Operador = null,
        int? DiasHastaLaDemostracion = null)
    {
        public MedicionTenant De(TenantPilotoOutbound tenant) => Tenants.Single(t => t.Clave == tenant.Clave);
    }

    /// <summary>Mide con las cuentas de la siembra local.</summary>
    public static Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, OpcionesPilotoOutbound opciones, CancellationToken cancellationToken = default) =>
        MedirAsync(fabricaDeAmbitos, CuentasPilotoOutbound.Locales, opciones, cancellationToken);

    /// <summary>
    /// Mide con las cuentas indicadas, que tienen que ser la Gestora CAE primera y la
    /// Coordinadora CAE sembradas en el Tenant del Operador CAE externo del piloto
    /// (ver <see cref="ComoCuentaDelPilotoAsync{T}"/>). Solo lectura.
    /// </summary>
    internal static Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, OpcionesPilotoOutbound opciones,
        CancellationToken cancellationToken) =>
        MedirAsync(fabricaDeAmbitos, cuentas, opciones, tenantsQueNoSeMiden: [], cancellationToken);

    /// <summary>
    /// La misma medición, sin los Tenants propietarios de <paramref name="tenantsQueNoSeMiden"/>
    /// (por su nombre del catálogo): no se abre su ámbito ni se hace ninguna de sus lecturas, y
    /// el informe no los trae.
    ///
    /// <para>
    /// Las dos lecturas que recorren la cartera entera se hacen igual, porque son consultas de
    /// pantalla y no se parten: Mi trabajo, que degrada por Tenant y no lanza por lo que uno
    /// lleve, y Visión de cartera, que ejecuta la consulta de Inicio en cada Tenant de la cartera
    /// de la Coordinadora CAE y sí lanza si esa consulta falla en alguno, también en uno que
    /// aquí no se mide. De lo que traen de un Tenant que no se mide no se usa nada.
    /// </para>
    /// </summary>
    internal static async Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, OpcionesPilotoOutbound opciones,
        IReadOnlyCollection<string> tenantsQueNoSeMiden, CancellationToken cancellationToken)
    {
        var comoGestora = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.GestoraPrimera, Roles.GestorCae,
            async servicios => (
                Tenants: await MedirComoGestoraAsync(servicios, opciones, tenantsQueNoSeMiden, cancellationToken),
                AsistenteIa: await MedirCarteraDelAsistenteIaAsync(servicios, cancellationToken)),
            cancellationToken);

        var comoCoordinadora = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.Coordinadora, Roles.CoordinadorCae,
            async servicios => (
                VisionCartera: (await servicios.GetRequiredService<ISender>().Send(new ObtenerKpisGlobalesQuery(), cancellationToken))
                    .ClientesConMasRiesgo.ToDictionary(c => c.Nombre),
                AsistenteIa: await MedirCarteraDelAsistenteIaAsync(servicios, cancellationToken),
                PendientesActivacion: (await servicios.GetRequiredService<ISender>().Send(
                    new ObtenerEquipoDeCoordinadorQuery(), cancellationToken)).Count(m => m.PendienteActivacion)),
            cancellationToken);
        var visionCartera = comoCoordinadora.VisionCartera;

        return new Informe(
        [
            .. comoGestora.Tenants.Select(m => m with
            {
                VisionCarteraPresente = visionCartera.ContainsKey(m.Nombre),
                VisionCarteraCumplimiento = visionCartera.GetValueOrDefault(m.Nombre)?.TasaCumplimientoDocumental,
                VisionCarteraSinCartera = visionCartera.GetValueOrDefault(m.Nombre)?.SinCarteraAsignada ?? false,
                VisionCarteraSinDatos = visionCartera.GetValueOrDefault(m.Nombre)?.SinDatos ?? false
            })
        ],
        new MedicionAsistenteIa(comoGestora.AsistenteIa, comoCoordinadora.AsistenteIa),
        new MedicionOperador(comoCoordinadora.PendientesActivacion),
        DiasHastaLaDemostracion: opciones.FechaDemostracion.DayNumber - DiaDeNegocio.Hoy().DayNumber);
    }

    /// <summary>
    /// La comprobación de cartera del Asistente IA con la identidad en curso: la misma consulta que el asistente
    /// hace antes de enviar nada al proveedor. Recorre la cartera entera, también los Tenants que esta medición
    /// deja sin medir, porque no se parte; de cada uno lee solo si tiene una instrucción vigente.
    /// </summary>
    private static async Task<CarteraDelAsistenteIa> MedirCarteraDelAsistenteIaAsync(
        IServiceProvider servicios, CancellationToken cancellationToken)
    {
        var cartera = await servicios.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery(), cancellationToken);

        return new CarteraDelAsistenteIa(
            [.. cartera.ConInstruccion.Select(t => t.Nombre)], [.. cartera.SinInstruccion.Select(t => t.Nombre)]);
    }

    /// <summary>
    /// Todas las discrepancias entre lo medido y la matriz, cada una con su Tenant
    /// y su contador. Vacía si la siembra está como la matriz dice.
    /// </summary>
    public static IReadOnlyList<string> Discrepancias(Informe informe) => Repartir(informe, tenantsQueNoSeMiden: []).Exigidas;

    /// <summary>
    /// Las discrepancias entre lo medido y la matriz, repartidas entre las que se
    /// exigen y las que solo se avisan.
    ///
    /// <para>
    /// Todas son de un solo Tenant propietario menos una, que es del lote entero: el
    /// control positivo de «cero filas» de Mi trabajo, que mira a la vez todos los
    /// Tenants medidos. Si no se ha dejado ninguno sin medir, se exigen todas. De un
    /// Tenant de <paramref name="tenantsQueNoSeMiden"/> no sale ninguna —ni la de
    /// «no se pudo medir»—, y lo del lote entero entonces solo se avisa, porque
    /// depende de lo que ese Tenant lleve. Lo de cualquier otro Tenant se exige igual
    /// que siempre: lo único que exime a un Tenant es estar en esa lista.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<string> Exigidas, IReadOnlyList<string> SoloAvisadas) Repartir(
        Informe informe, IReadOnlyCollection<string> tenantsQueNoSeMiden)
    {
        var d = new List<string>();

        foreach (var tenant in CatalogoPilotoOutbound.Tenants.Where(t => !tenantsQueNoSeMiden.Contains(t.Nombre)))
        {
            if (informe.Tenants.SingleOrDefault(t => t.Clave == tenant.Clave) is not { } m)
            {
                d.Add($"{tenant.Clave} «{tenant.Nombre}»: el Tenant no existe o no se pudo medir.");
                continue;
            }

            void Discrepa(string contador, string medido, string esperado) =>
                d.Add($"{tenant.Clave} «{tenant.Nombre}» · {contador}: medido {medido}, esperado {esperado}.");

            void Exige<T>(string contador, T medido, T esperado)
            {
                if (!EqualityComparer<T>.Default.Equals(medido, esperado))
                    Discrepa(contador, Texto(medido), Texto(esperado));
            }

            // Comunes a los siete: la cola se pudo consultar, cada documento abre como PDF, tiene las fechas que el
            // producto le habría dado y «Pedir» tiene a quién escribir.
            Exige("Mi trabajo · Tenant presente en la cartera de la Gestora CAE", m.MiTrabajoPresente, true);
            Exige("Mi trabajo · cola que no se pudo consultar", m.MiTrabajoNoConsultado, false);
            Exige("Documentos · sin PDF que se abra", m.DocumentosSinPdf, 0);
            Exige("Documentos · vencimiento que no es la emisión más los meses de su Tipo", m.DocumentosConVencimientoAjenoASuTipo, 0);
            Exige("Documentos · emisión posterior a hoy", m.DocumentosEmitidosEnElFuturo, 0);
            Exige("Agenda · contactos fuera de la regla de correo", m.ContactosFueraDeLaReglaDeCorreo, 0);
            Exige("Clientes empresariales · sin contacto en la agenda", m.ClientesEmpresarialesSinContacto, 0);
            Exige("Agenda · tiene contactos", m.ContactosDeAgenda > 0, true);

            // «Cero filas» solo vale con alcance: un Tenant sin cartera también daría cero.
            Exige("Mi trabajo · alcance cero", m.MiTrabajoAlcanceCero, false);
            Exige("Visión de cartera · Tenant presente para la Coordinadora CAE", m.VisionCarteraPresente, true);
            // Con cualquiera de las dos, la fila pinta «Sin cartera» o «Sin datos» en vez del porcentaje.
            Exige("Visión de cartera · fila sin cartera asignada", m.VisionCarteraSinCartera, false);
            Exige("Visión de cartera · fila sin datos", m.VisionCarteraSinDatos, false);

            var esperado = CatalogoPilotoOutbound.Esperado(tenant);
            void Dentro(string contador, int? medido, IntervaloPilotoOutbound intervalo)
            {
                if (!intervalo.Contiene(medido)) Discrepa(contador, Texto(medido), intervalo.ToString());
            }

            Dentro("Inicio · % de cumplimiento", m.InicioCumplimiento, esperado.CumplimientoInicio);
            Dentro("Empresas · % de cumplimiento de la Empresa propia", m.EmpresaCumplimiento, esperado.CumplimientoEmpresa);
            Exige("Centros · número de Centros", m.Centros.Count, tenant.Centros.Count);
            Exige(
                "Centros · Centros «Vigente» (al menos los que el perfil declara)",
                m.Centros.Count(c => c.Estado == EstadoCentro.Vigente) >= esperado.CentrosVigentesMinimo, true);
            Exige("Clientes empresariales · número", m.ClientesEmpresariales, tenant.ClientesEmpresariales.Count);
            Exige("Documentos · hay documentos", m.Documentos > 0, true);

            var e = m.Estructura;
            var supervisadas = tenant.Subcontratas.Count(x => x.Supervisada);
            Exige("Empresas · Empresas con EsPropia (exactamente la del Tenant)", e.EmpresasPropias, 1);
            Exige("Centros · Centros que no cuelgan de un Cliente empresarial del Tenant", e.CentrosFueraDeUnClienteDelTenant, 0);
            Exige("Empresas · Empresas que la Gestora CAE no ve", e.EmpresasNoVisiblesParaLaGestora, 0);
            Exige("Trabajadores · propios", e.TrabajadoresPropios, tenant.Trabajadores);
            Exige("Trabajadores · de Subcontrata", e.TrabajadoresDeSubcontrata, tenant.Subcontratas.Sum(x => x.Trabajadores));
            Exige("Trabajadores · sin ficha completa (afiliación, nacionalidad, vínculo, alta)", e.TrabajadoresSinFicha, 0);
            Exige("Trabajadores · extranjeros con permiso de trabajo", e.ExtranjerosConPermiso, tenant.Extranjeros.Count);
            Exige("Trabajadores · permisos de trabajo caducados", e.PermisosCaducados, tenant.Extranjeros.Count(x => x.DiasHastaLaCaducidad < 0));
            Exige(
                "Trabajadores · permisos de trabajo próximos a caducar",
                e.PermisosProximosACaducar, tenant.Extranjeros.Count(x => x.DiasHastaLaCaducidad is >= 0 and <= 45));
            Exige("Vehículos · número", e.Vehiculos, tenant.Vehiculos.Count);
            Exige("Vehículos · sin ficha (tipo, titularidad, aseguradora, póliza)", e.VehiculosSinFicha, 0);
            Exige("Vehículos · sin conductor autorizado", e.VehiculosSinConductor, 0);
            Exige("Proyectos · número", e.Proyectos, tenant.Obras.Count);
            Exige("Proyectos · abiertos", e.ProyectosAbiertos, tenant.Obras.Count(x => !x.Cerrada));
            Exige("Subcontratas · número", e.Subcontratas, tenant.Subcontratas.Count);
            Exige("Subcontratas · sin Trabajadores", e.SubcontratasSinTrabajadores, 0);
            Exige("Subcontratas · Supervisadas", e.SubcontratasSupervisadas, supervisadas);
            Exige(
                "Subcontratas · verificaciones externas",
                e.VerificacionesExternas, supervisadas * CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata.Count);
            Exige("Visitas · futuras", e.VisitasFuturas, tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.Futura));
            Exige("Visitas · realizadas", e.VisitasRealizadas, tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.Realizada));
            Exige("Visitas · vencidas", e.VisitasVencidas, tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.Vencida));
            Exige("Visitas · canceladas", e.VisitasCanceladas, tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.Cancelada));
            Exige("Visitas · en curso", e.VisitasEnCurso, tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.EnCurso));

            // Lo que depende del día de medición: con la distancia a la demostración conocida, lo que Inicio cuenta como
            // urgente tiene que ser lo que el catálogo dice para ese día, y coincidir con lo que dicen las filas de las Visitas.
            if (informe.DiasHastaLaDemostracion is { } diasHastaLaDemostracion)
            {
                Exige(
                    "Visitas · con aviso urgente (empiezan dentro de las 48 h de aviso)",
                    e.VisitasConAvisoUrgente,
                    tenant.Visitas.Count(x => x.Estado == EstadoVisitaPilotoOutbound.Futura
                                              && x.DiasDesdeLaDemostracion + diasHastaLaDemostracion is >= 1 and <= 2));
                Exige(
                    "Inicio · Visitas urgentes (en curso y con aviso urgente)",
                    m.InicioVisitasUrgentes, CatalogoPilotoOutbound.VisitasUrgentesEn(tenant, diasHastaLaDemostracion));
            }

            // Coherencia de fechas y los tipos del catálogo de construcción y autónomos.
            Exige("Trabajadores · con el alta en la Empresa posterior a un documento de su relación laboral", e.TrabajadoresConAltaPosteriorAUnDocumento, 0);
            Exige("Proyectos · Libros de Subcontratación (uno por obra)", e.LibrosDeSubcontratacion, tenant.Obras.Count);
            Exige("Empresas · certificado de inscripción en el REA", e.CertificadosRea, tenant.Perfil == PerfilPilotoOutbound.ObrasYMaquinaria ? 1 : 0);
            Exige("Empresas · recibo de cuota de autónomos (RETA)", e.RecibosDeAutonomos, tenant.Perfil == PerfilPilotoOutbound.Autonomo ? 1 : 0);

            // Lo que Inicio cuenta de los documentos de Trabajador, contra lo que la receta del perfil dice (deducido del
            // catálogo, no medido: lo realizado puede ser menos que lo pedido si no quedan pares compatibles).
            var inicioEsperado = CatalogoPilotoOutbound.DocumentosEsperadosEnInicio(tenant);
            Dentro("Inicio · documentos vencidos", m.InicioVencidos, inicioEsperado.Vencidos);
            Dentro("Inicio · documentos urgentes", m.InicioUrgentes, inicioEsperado.Urgentes);
            Dentro("Inicio · documentos próximos", m.InicioProximos, inicioEsperado.Proximos);
            Exige("Inicio · documentos sin confirmar", m.InicioSinConfirmar, inicioEsperado.SinConfirmar);

            Exige("Documentos · renovados (versión anterior en el historial)", e.DocumentosRenovados, tenant.ConDocumentoRenovado ? 1 : 0);
            Exige("Acreditaciones externas · pendientes de validar (subidas)", e.AcreditacionesSubidas, tenant.Receta.AcreditacionesSubidas);
            Exige("Acreditaciones externas · rechazadas", e.AcreditacionesRechazadas, tenant.Receta.AcreditacionesRechazadas);
            Exige("Acreditaciones externas · rechazadas sin motivo", e.AcreditacionesRechazadasSinMotivo, 0);
            Exige("Reclamaciones · sin respuesta", e.ReclamacionesSinRespuesta, tenant.ReclamacionesSinRespuesta);
            Exige("Reclamaciones · respondidas", e.ReclamacionesRespondidas, tenant.ReclamacionesRespondidas);
        }

        // Control positivo de «cero filas»: en la MISMA lectura de Mi trabajo, otro Tenant sí trae filas.
        // Es del lote entero: no se atribuye a ningún Tenant.
        var delLoteEntero = new List<string>();
        if (informe.Tenants.All(t => t.MiTrabajoFilas == 0))
            delLoteEntero.Add("Mi trabajo · control positivo: ningún Tenant del piloto trae filas, así que un cero no demuestra nada.");

        // Lo del Tenant del Operador CAE externo depende de T4 y T5 (la propuesta y la solicitud van sobre sus
        // operaciones): es del lote entero, y con un Tenant sin medir solo se avisa.
        if (informe.Operador is not { } operador)
            delLoteEntero.Add("Operador CAE externo · no se midió.");
        else
        {
            void ExigeOperador(string contador, int medido) =>
                delLoteEntero.AddRange(medido == 1 ? [] : [$"Operador CAE externo · {contador}: medido {medido}, esperado 1."]);
            ExigeOperador("cuentas del equipo de la Coordinadora CAE pendientes de activación", operador.PendientesActivacionEquipo);
        }

        var (asistenteExigidas, asistenteSoloAvisadas) = RepartirAsistenteIa(informe.AsistenteIa, tenantsQueNoSeMiden);
        d.AddRange(asistenteExigidas);

        return tenantsQueNoSeMiden.Count == 0
            ? ([.. d, .. delLoteEntero], asistenteSoloAvisadas)
            : (d, [.. delLoteEntero, .. asistenteSoloAvisadas]);
    }

    /// <summary>
    /// Lo que se exige del Asistente IA: que su comprobación de cartera pase para la Gestora CAE y para la
    /// Coordinadora CAE. Para cada una, el Tenant del Operador CAE externo y cada Tenant propietario que se mide
    /// tienen que estar en su cartera con una instrucción de tratamiento de IA vigente —por nombre, no por
    /// recuento: una cartera vacía también «pasaría»—, y no puede haber en ella ningún otro Tenant sin instrucción,
    /// porque con uno basta para que el asistente falle cerrado.
    ///
    /// <para>
    /// La única que solo se avisa es la de un Tenant de <paramref name="tenantsQueNoSeMiden"/> sin instrucción: la
    /// siembra no escribe en él, tampoco su instrucción, y mientras siga en la cartera el asistente falla cerrado.
    /// No haber medido se exige como discrepancia.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<string> Exigidas, IReadOnlyList<string> SoloAvisadas) RepartirAsistenteIa(
        MedicionAsistenteIa? asistenteIa, IReadOnlyCollection<string> tenantsQueNoSeMiden)
    {
        if (asistenteIa is null)
            return (["Asistente IA · instrucción de tratamiento de IA de la cartera: no se midió."], []);

        var exigidas = new List<string>();
        var soloAvisadas = new List<string>();
        List<string> esperados =
        [
            CatalogoPilotoOutbound.NombreTenantOperador,
            .. CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).Where(n => !tenantsQueNoSeMiden.Contains(n))
        ];

        foreach (var (cuenta, cartera) in new[] { ("Gestora CAE", asistenteIa.Gestora), ("Coordinadora CAE", asistenteIa.Coordinadora) })
        {
            foreach (var nombre in esperados.Where(n => !cartera.ConInstruccion.Contains(n)))
                exigidas.Add(
                    $"Asistente IA · cartera de la {cuenta} · «{nombre}»: medido " +
                    (cartera.SinInstruccion.Contains(nombre) ? "sin instrucción de tratamiento de IA vigente" : "fuera de la cartera") +
                    ", esperado con instrucción de tratamiento de IA vigente.");

            foreach (var nombre in cartera.SinInstruccion.Where(n => !esperados.Contains(n)))
                (tenantsQueNoSeMiden.Contains(nombre) ? soloAvisadas : exigidas).Add(
                    $"Asistente IA · cartera de la {cuenta} · «{nombre}»: está en la cartera sin instrucción de tratamiento de IA " +
                    "vigente, y con eso el asistente no envía ningún mensaje.");
        }

        return (exigidas, soloAvisadas);
    }

    /// <summary>
    /// Las divergencias declaradas del catálogo, con lo que se ha medido: no hacen
    /// fallar, pero quien prepara la demostración tiene que leerlas. Hay una por
    /// cada Tenant cuyo valor esperado de Inicio o de Visión de cartera no coincide
    /// con el de Empresas, y solo mientras no coincida: el día que el catálogo los
    /// iguale, la advertencia desaparece sin tocar nada aquí.
    /// </summary>
    public static IReadOnlyList<string> Advertencias(Informe informe) => [];

    /// <summary>Lanza con TODAS las discrepancias si lo medido no es lo que la matriz declara.</summary>
    public static void Exigir(Informe informe) => LanzarSiHay(Discrepancias(informe));

    private static void LanzarSiHay(IReadOnlyList<string> discrepancias)
    {
        if (discrepancias.Count > 0)
            throw new InvalidOperationException(
                $"La siembra del piloto Outbound no da los resultados de su matriz ({discrepancias.Count} discrepancias):" +
                Environment.NewLine + string.Join(Environment.NewLine, discrepancias.Select(x => " - " + x)));
    }

    /// <summary>
    /// El paso del arranque local tras la siembra: mide con las cuentas locales y decide
    /// según <paramref name="escribio"/> y <paramref name="tenantsConDatosDeOtraVersion"/>,
    /// que son los dos datos del resultado de esa siembra (<see cref="PilotoOutboundSeeder.Resultado"/>).
    /// </summary>
    public static Task MedirYExigirOAvisarAsync(
        IServiceScopeFactory fabricaDeAmbitos, OpcionesPilotoOutbound opciones, bool escribio,
        IReadOnlyList<string> tenantsConDatosDeOtraVersion, ILogger logger, CancellationToken cancellationToken = default) =>
        MedirYExigirOAvisarAsync(
            fabricaDeAmbitos, CuentasPilotoOutbound.Locales, opciones, escribio, tenantsConDatosDeOtraVersion, logger, cancellationToken);

    /// <summary>
    /// Mide y, según la ejecución haya escrito o no, exige o avisa.
    ///
    /// <para>
    /// <b>Si acaba de escribir</b>, lo medido tiene que ser la matriz: una discrepancia lanza
    /// y no poder medir también, porque una siembra recién hecha que no se deja medir está mal.
    /// </para>
    ///
    /// <para>
    /// <b>Si acaba de escribir y hay un Tenant propietario con datos de otra versión de la
    /// siembra</b> —la siembra lo dejó como estaba y escribió los que faltaban—, ese Tenant no
    /// se mide: se avisa, con su nombre, de que no se mide ni se le exige la matriz, y no se
    /// hace ninguna de sus lecturas, así que tampoco tumba el arranque que no se deje medir. Lo
    /// del lote entero, que depende de lo que él lleve, solo se avisa (<see cref="Repartir"/>).
    /// Los demás Tenants se miden y se exigen igual que siempre: una discrepancia suya lanza, y
    /// no poder medirlos, también.
    /// </para>
    ///
    /// <para>
    /// <b>Qué Tenants son de otra versión no se averigua aquí.</b> Es
    /// <paramref name="tenantsConDatosDeOtraVersion"/>, la lista que la siembra calculó antes de
    /// escribir nada (<see cref="PilotoOutboundSeeder.Resultado.TenantsConDatosDeOtraVersion"/>).
    /// Leerlo después de sembrar haría depender la autoverificación de los mismos datos que
    /// verifica: un Tenant recién escrito sin la Empresa propia que se espera pasaría por uno de
    /// otra versión y quedaría eximido. Un Tenant que no está en la lista se mide y se exige,
    /// lleve lo que lleve.
    /// </para>
    ///
    /// <para>
    /// <b>Si no escribió</b> (un re-arranque con el lote ya sembrado), nada de esto lanza y se
    /// miden todos los Tenants, también los de otra versión. Tras un ensayo los datos cambian a
    /// propósito —se descarta un documento, se cambia el rol de una cuenta, se borra un Centro—,
    /// y eso puede dar discrepancias o impedir la medición entera (la cuenta con la que se mide
    /// ya no tiene su rol, falta la fila que una lectura espera única). Las dos cosas quedan
    /// como advertencias en el registro y el arranque sigue.
    /// </para>
    /// </summary>
    internal static async Task MedirYExigirOAvisarAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, OpcionesPilotoOutbound opciones, bool escribio,
        IReadOnlyList<string> tenantsConDatosDeOtraVersion, ILogger logger, CancellationToken cancellationToken)
    {
        // Solo una ejecución que escribió deja Tenants sin medir: un re-arranque mide todo y de todo avisa.
        IReadOnlyList<string> sinMedir = escribio ? tenantsConDatosDeOtraVersion : [];

        try
        {
            foreach (var nombre in sinMedir)
                logger.LogWarning(
                    "Piloto Outbound, el Tenant «{Tenant}» no se mide ni se le exige la matriz: tiene datos de otra versión " +
                    "de la siembra y esta ejecución no ha escrito en él.", nombre);

            var informe = await MedirAsync(fabricaDeAmbitos, cuentas, opciones, sinMedir, cancellationToken);

            foreach (var advertencia in Advertencias(informe))
                logger.LogWarning("Piloto Outbound, divergencia declarada: {Advertencia}", advertencia);

            if (escribio)
            {
                var (exigidas, soloAvisadas) = Repartir(informe, sinMedir);

                foreach (var discrepancia in soloAvisadas)
                    logger.LogWarning(
                        "Piloto Outbound, discrepancia del lote entero que no se exige porque hay datos de otra versión " +
                        "de la siembra en «{Tenants}»: {Discrepancia}", string.Join("», «", sinMedir), discrepancia);

                LanzarSiHay(exigidas);
                return;
            }

            foreach (var discrepancia in Discrepancias(informe))
                logger.LogWarning("Piloto Outbound, los datos ya no son los de la matriz: {Discrepancia}", discrepancia);
        }
        // Solo en el re-arranque, y solo lo que no sea una cancelación: con el lote recién escrito, todo lanza.
        catch (Exception ex) when (!escribio && ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Piloto Outbound, no se ha podido medir el lote ya sembrado y el arranque sigue sin esa comprobación: {Motivo}",
                ex.Message);
        }
    }

    private static string Texto<T>(T valor) => valor is null ? "(nada)" : valor.ToString()!;

    private static async Task<IReadOnlyList<MedicionTenant>> MedirComoGestoraAsync(
        IServiceProvider servicios, OpcionesPilotoOutbound opciones, IReadOnlyCollection<string> tenantsQueNoSeMiden,
        CancellationToken cancellationToken)
    {
        var contactos = opciones.Contactos;
        var sender = servicios.GetRequiredService<ISender>();
        var dbContext = servicios.GetRequiredService<CaeManagerDbContext>();
        var calculo = servicios.GetRequiredService<ICalculoEstadoCentroService>();
        var almacen = servicios.GetRequiredService<IFileStorageService>();
        var hoy = DiaDeNegocio.Hoy();

        var nombres = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).ToList();
        var idPorNombre = await dbContext.Tenants.AsNoTracking()
            .Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Nombre, t => t.Id, cancellationToken);

        // Una sola lectura de Mi trabajo para toda la cartera, como la pantalla.
        var miTrabajo = await sender.Send(new ObtenerMiTrabajoAgregadoQuery(), cancellationToken);

        var mediciones = new List<MedicionTenant>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            // Ni su ámbito ni ninguna de sus lecturas: lo que lleve no puede hacer lanzar a esta medición.
            if (tenantsQueNoSeMiden.Contains(tenant.Nombre)) continue;
            if (!idPorNombre.TryGetValue(tenant.Nombre, out var tenantId)) continue;

            var cola = miTrabajo.Tenants.SingleOrDefault(t => t.TenantId == tenantId);
            var noConsultado = miTrabajo.NoConsultados.Any(t => t.TenantId == tenantId);

            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var kpis = await sender.Send(new ObtenerKpisDashboardQuery(), cancellationToken);
                var centros = (await sender.Send(new ObtenerCentrosQuery(null, null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var empresas = (await sender.Send(new ObtenerEmpresasQuery(null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var clientes = (await sender.Send(new ObtenerClientesQuery(null, null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var pares = await calculo.ObtenerParesExigidosAsync([.. centros.Select(c => c.Id)], cancellationToken);

                var documentos = await dbContext.Documentos.AsNoTracking()
                    .Select(d => new { d.ArchivoUrl, d.TipoDocumentoId, d.FechaEmision, d.FechaVencimiento }).ToListAsync(cancellationToken);
                var sinPdf = 0;
                foreach (var clave in documentos.Select(d => d.ArchivoUrl))
                    if (clave is null || !await EsPdfAsync(almacen, clave, cancellationToken))
                        sinPdf++;

                // Lo que el producto no deja crear: un Tipo que vence solo, con un vencimiento que no sale de su emisión.
                var tipos = await dbContext.TiposDocumento.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken);
                var conVencimientoAjenoASuTipo = documentos.Count(d =>
                    tipos[d.TipoDocumentoId] is { FijaVigenciaDesdeLaEmision: true } tipo
                    && d.FechaVencimiento != CalculadoraEstadoDocumento.CalcularFechaVencimiento(d.FechaEmision, tipo.VigenciaMeses));
                var emitidosEnElFuturo = documentos.Count(d => d.FechaEmision > hoy);

                var correos = await dbContext.ContactosAgenda.AsNoTracking().Select(c => c.Email).ToListAsync(cancellationToken);

                var estructura = await MedirEstructuraAsync(servicios, dbContext, cancellationToken);

                mediciones.Add(new MedicionTenant(
                    tenant.Clave, tenant.Nombre,
                    MiTrabajoPresente: cola is not null || noConsultado,
                    MiTrabajoNoConsultado: noConsultado,
                    MiTrabajoAlcanceCero: cola?.AlcanceCero ?? false,
                    MiTrabajoBloqueos: cola?.Resumen.Bloqueos ?? 0,
                    MiTrabajoActuaciones: cola?.Resumen.Actuaciones ?? 0,
                    MiTrabajoProximos: cola?.Resumen.Proximos ?? 0,
                    MiTrabajoSeguimiento: cola?.Resumen.Seguimiento ?? 0,
                    InicioCumplimiento: kpis.TasaCumplimientoDocumental,
                    InicioVencidos: kpis.DocumentosVencidos,
                    InicioUrgentes: kpis.DocumentosUrgentes,
                    InicioProximos: kpis.DocumentosProximos,
                    InicioSinConfirmar: kpis.DocumentosSinConfirmar,
                    InicioCentrosBloqueados: kpis.CentrosBloqueados,
                    InicioTrabajadoresBloqueados: kpis.TrabajadoresBloqueados,
                    InicioVisitasUrgentes: kpis.VisitasUrgentes,
                    VisionCarteraPresente: false,
                    VisionCarteraCumplimiento: null,
                    VisionCarteraSinCartera: false,
                    VisionCarteraSinDatos: false,
                    Centros: [.. centros.Select(c => new MedicionCentro(c.Nombre, c.Estado, c.CumplimientoPorcentaje))],
                    EmpresaCumplimiento: empresas.SingleOrDefault(e => e.RazonSocial == tenant.Nombre)?.CumplimientoPorcentaje,
                    ParesExigidos: pares.Count,
                    ParesFaltantes: pares.Count(p => p.Estado == EstadoDocumento.Faltante),
                    ClientesEmpresariales: clientes.Count,
                    ClientesEmpresarialesSinContacto: clientes.Count(c => c.SinContactoEnAgenda),
                    ClientesEmpresarialesConAlertas: clientes.Count(c => c.EstadoDocumentalPeor is not null || c.EstadoDocumentalCantidad != 0),
                    Documentos: documentos.Count,
                    DocumentosSinPdf: sinPdf,
                    DocumentosConVencimientoAjenoASuTipo: conVencimientoAjenoASuTipo,
                    DocumentosEmitidosEnElFuturo: emitidosEnElFuturo,
                    ContactosDeAgenda: correos.Count,
                    ContactosFueraDeLaReglaDeCorreo: correos.Count(c => !contactos.Cumple(c)),
                    Estructura: estructura));
            }
        }

        return mediciones;
    }

    /// <summary>
    /// La estructura del Tenant, dentro del ámbito de Tenant que ya abrió quien llama y con la identidad de la Gestora CAE.
    /// Solo lectura. Las visitas se clasifican por sus datos, como en la aplicación (fechas, documentación gestionada y
    /// cancelación), contra el día en que se mide.
    /// </summary>
    private static async Task<MedicionEstructura> MedirEstructuraAsync(
        IServiceProvider servicios, CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        var empresas = await dbContext.Empresas.AsNoTracking()
            .Select(x => new { x.Id, x.EsPropia, x.NivelServicio }).ToListAsync(cancellationToken);
        var clienteIds = empresas.Where(x => !x.EsPropia && x.NivelServicio == null).Select(x => x.Id).ToHashSet();
        var subcontratas = empresas.Where(x => x.NivelServicio != null).ToList();
        var centros = await dbContext.Centros.AsNoTracking().Select(x => x.ClienteId).ToListAsync(cancellationToken);
        var trabajadores = await dbContext.Trabajadores.AsNoTracking().Select(t => new
        {
            t.Id,
            t.EmpresaId,
            t.SubcontrataId,
            t.NumeroAfiliacionSeguridadSocial,
            t.Nacionalidad,
            t.TipoVinculo,
            t.FechaAltaEmpresa,
            t.TipoPermisoTrabajo,
            t.CaducidadPermisoTrabajo
        }).ToListAsync(cancellationToken);
        var vehiculos = await dbContext.Vehiculos.AsNoTracking()
            .Select(v => new { v.Id, v.Tipo, v.Titularidad, v.Aseguradora, v.Poliza }).ToListAsync(cancellationToken);
        var conConductor = (await dbContext.Set<VehiculoTrabajador>().AsNoTracking().Select(c => c.VehiculoId).ToListAsync(cancellationToken)).ToHashSet();
        var proyectos = await dbContext.Proyectos.AsNoTracking().Select(x => x.FechaCierreReal).ToListAsync(cancellationToken);
        var visitas = await dbContext.Visitas.AsNoTracking()
            .Select(v => new { v.FechaInicio, v.FechaFin, v.EstaCancelada, v.DocumentacionGestionadaEnUtc }).ToListAsync(cancellationToken);
        var acreditaciones = await dbContext.AcreditacionesDocumentoPlataforma.AsNoTracking()
            .Select(a => new { a.Id, a.Estado }).ToListAsync(cancellationToken);
        var rechazosConMotivo = (await dbContext.RechazosAcreditacionDocumentoPlataforma.AsNoTracking()
            .Where(r => r.MotivoLiteral != "").Select(r => r.AcreditacionId).ToListAsync(cancellationToken)).ToHashSet();
        var renovados = await dbContext.Documentos.AsNoTracking()
            .Where(x => x.TrabajadorId != null && x.MotivoSustitucion == MotivoSustitucionDocumento.Renovacion
                        && dbContext.Documentos.Any(n => n.Id == x.SustituidoPorDocumentoId && n.MotivoSustitucion == null
                                                         && n.TrabajadorId == x.TrabajadorId && n.TipoDocumentoId == x.TipoDocumentoId))
            .CountAsync(cancellationToken);
        var reclamaciones = await dbContext.ReclamacionesDocumentales.AsNoTracking()
            .Where(r => r.ConversacionId != null).Select(r => new { r.ConversacionId, r.FechaEnvioUtc }).ToListAsync(cancellationToken);
        var respuestas = await dbContext.Mensajes.AsNoTracking().Where(x => x.Direccion == DireccionMensaje.Entrante)
            .Select(x => new { x.ConversacionId, x.FechaUtc }).ToListAsync(cancellationToken);
        bool Respondida(Guid? conversacionId, DateTime enviada) =>
            respuestas.Any(x => x.ConversacionId == conversacionId && x.FechaUtc > enviada);

        // La misma regla que usan las pantallas para decidir qué Empresas ve la Gestora CAE: null es «sin restricción».
        var visibles = await servicios.GetRequiredService<IAlcanceDatosService>().ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var noVisibles = visibles is null ? 0 : empresas.Count(x => !visibles.Contains(x.Id));

        var extranjeros = trabajadores.Where(t => t.TipoPermisoTrabajo != null).ToList();

        // Coherencia de fechas: nadie causa alta en la Empresa después de la emisión de un documento de su relación laboral
        // (formación, aptitud, información, entrega de EPI), tampoco de una versión que luego se sustituyó.
        var nombresPosterioresAlAlta = CatalogoPilotoOutbound.TiposPosterioresAlAlta.ToList();
        var tiposPosterioresAlAlta = await dbContext.TiposDocumento.AsNoTracking()
            .Where(t => nombresPosterioresAlAlta.Contains(t.Nombre)).Select(t => t.Id).ToListAsync(cancellationToken);
        var primeraEmisionPorTrabajador = (await dbContext.Documentos.AsNoTracking()
                .Where(d => d.TrabajadorId != null && tiposPosterioresAlAlta.Contains(d.TipoDocumentoId))
                .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.FechaEmision }).ToListAsync(cancellationToken))
            .GroupBy(d => d.TrabajadorId).ToDictionary(g => g.Key, g => g.Min(d => d.FechaEmision));
        var conAltaPosterior = trabajadores.Count(t =>
            t.FechaAltaEmpresa is { } alta && primeraEmisionPorTrabajador.TryGetValue(t.Id, out var primera) && alta > primera);

        // Los tipos del catálogo de #1363, en uso (no sustituidos): uno por obra, uno en el perfil de obras, uno en el autónomo.
        async Task<int> EnUsoDeAsync(string nombreDeTipo)
        {
            var tipoId = await dbContext.TiposDocumento.AsNoTracking().Where(t => t.Nombre == nombreDeTipo).Select(t => t.Id).SingleAsync(cancellationToken);
            return await dbContext.Documentos.AsNoTracking().Operativos().CountAsync(d => d.TipoDocumentoId == tipoId, cancellationToken);
        }

        return new MedicionEstructura(
            TrabajadoresPropios: trabajadores.Count(t => t.EmpresaId != null),
            TrabajadoresDeSubcontrata: trabajadores.Count(t => t.SubcontrataId != null),
            TrabajadoresSinFicha: trabajadores.Count(t =>
                t.NumeroAfiliacionSeguridadSocial is null || t.Nacionalidad is null || t.TipoVinculo is null || t.FechaAltaEmpresa is null),
            ExtranjerosConPermiso: extranjeros.Count,
            PermisosCaducados: extranjeros.Count(t => t.CaducidadPermisoTrabajo < hoy),
            PermisosProximosACaducar: extranjeros.Count(t => t.CaducidadPermisoTrabajo >= hoy && t.CaducidadPermisoTrabajo <= hoy.AddDays(45)),
            Vehiculos: vehiculos.Count,
            VehiculosSinFicha: vehiculos.Count(v => v.Tipo is null || v.Titularidad is null || v.Aseguradora is null || v.Poliza is null),
            VehiculosSinConductor: vehiculos.Count(v => !conConductor.Contains(v.Id)),
            Proyectos: proyectos.Count,
            ProyectosAbiertos: proyectos.Count(cierre => cierre is null),
            Subcontratas: subcontratas.Count,
            SubcontratasSinTrabajadores: subcontratas.Count(s => trabajadores.All(t => t.SubcontrataId != s.Id)),
            SubcontratasSupervisadas: subcontratas.Count(s => s.NivelServicio == NivelServicioSubcontrata.Supervisada.ToString()),
            VerificacionesExternas: await dbContext.VerificacionesExternaSubcontrata.AsNoTracking().CountAsync(cancellationToken),
            EmpresasPropias: empresas.Count(x => x.EsPropia),
            CentrosFueraDeUnClienteDelTenant: centros.Count(clienteId => !clienteIds.Contains(clienteId)),
            EmpresasNoVisiblesParaLaGestora: noVisibles,
            VisitasFuturas: visitas.Count(v => !v.EstaCancelada && v.FechaInicio > hoy),
            VisitasRealizadas: visitas.Count(v => !v.EstaCancelada && v.FechaFin < hoy && v.DocumentacionGestionadaEnUtc is not null),
            VisitasVencidas: visitas.Count(v => !v.EstaCancelada && v.FechaFin < hoy && v.DocumentacionGestionadaEnUtc is null),
            VisitasCanceladas: visitas.Count(v => v.EstaCancelada),
            VisitasEnCurso: visitas.Count(v => !v.EstaCancelada && v.FechaInicio <= hoy && hoy <= v.FechaFin),
            VisitasConAvisoUrgente: visitas.Count(v => !v.EstaCancelada && v.FechaInicio > hoy && v.FechaInicio <= hoy.AddDays(2)),
            TrabajadoresConAltaPosteriorAUnDocumento: conAltaPosterior,
            LibrosDeSubcontratacion: await EnUsoDeAsync(CatalogoPilotoOutbound.LibroDeSubcontratacion),
            CertificadosRea: await EnUsoDeAsync(CatalogoPilotoOutbound.CertificadoReaConstruccion),
            RecibosDeAutonomos: await EnUsoDeAsync(CatalogoPilotoOutbound.ReciboDeAutonomos),
            DocumentosRenovados: renovados,
            AcreditacionesSubidas: acreditaciones.Count(a => a.Estado == EstadoAcreditacion.Subida),
            AcreditacionesRechazadas: acreditaciones.Count(a => a.Estado == EstadoAcreditacion.Rechazada),
            AcreditacionesRechazadasSinMotivo: acreditaciones.Count(a => a.Estado == EstadoAcreditacion.Rechazada && !rechazosConMotivo.Contains(a.Id)),
            ReclamacionesSinRespuesta: reclamaciones.Count(r => !Respondida(r.ConversacionId, r.FechaEnvioUtc)),
            ReclamacionesRespondidas: reclamaciones.Count(r => Respondida(r.ConversacionId, r.FechaEnvioUtc)));
    }

    private static async Task<bool> EmpiezaComoPdfAsync(Stream flujo, CancellationToken cancellationToken)
    {
        var cabecera = new byte[5];
        return await flujo.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken) == cabecera.Length
               && cabecera.AsSpan().SequenceEqual("%PDF-"u8);
    }

    private static async Task<bool> EsPdfAsync(IFileStorageService almacen, string clave, CancellationToken cancellationToken)
    {
        try
        {
            await using var flujo = await almacen.AbrirAsync(clave, cancellationToken);
            var cabecera = new byte[5];
            return await flujo.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken) == cabecera.Length
                   && cabecera.AsSpan().SequenceEqual("%PDF-"u8);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ejecuta una lectura con la identidad de una cuenta sembrada del piloto, en un
    /// ámbito de servicios nuevo y fuera de toda petición.
    ///
    /// <para>
    /// Antes de construir nada comprueba, leyendo la base, que la cuenta es del
    /// Tenant del Operador CAE externo del piloto (localizado por su nombre del
    /// catálogo), que ese Tenant lleva el marcador de datos de demo y puede actuar
    /// como Operador CAE externo, y que la cuenta tiene exactamente <paramref name="rolExigido"/>.
    /// La identidad es la que emite la fábrica de claims de la aplicación para esa
    /// cuenta —la misma que tendría tras iniciar sesión—, sin añadirle nada. Se
    /// publica en el accesor de contexto HTTP solo mientras corre
    /// <paramref name="lectura"/> y se retira en el <c>finally</c>.
    /// </para>
    /// </summary>
    private static async Task<T> ComoCuentaDelPilotoAsync<T>(
        IServiceScopeFactory fabricaDeAmbitos, string email, string rolExigido,
        Func<IServiceProvider, Task<T>> lectura, CancellationToken cancellationToken)
    {
        await using var ambito = fabricaDeAmbitos.CreateAsyncScope();
        var servicios = ambito.ServiceProvider;

        var accesor = servicios.GetRequiredService<IHttpContextAccessor>();
        if (accesor.HttpContext is not null)
            throw new InvalidOperationException("La autoverificación del piloto solo corre fuera de una petición HTTP.");

        var dbContext = servicios.GetRequiredService<CaeManagerDbContext>();
        var operador = await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Nombre == CatalogoPilotoOutbound.NombreTenantOperador)
            .Select(t => new { t.Id, t.DatosDemoCompletadosEnUtc, t.PuedeActuarComoOperadorCaeExterno })
            .SingleOrDefaultAsync(cancellationToken);

        if (operador is null || !operador.PuedeActuarComoOperadorCaeExterno || operador.DatosDemoCompletadosEnUtc is null)
            throw new InvalidOperationException(
                "La autoverificación del piloto solo mide con cuentas del Tenant del Operador CAE externo del piloto, y ese " +
                "Tenant no existe, no es un Operador CAE externo o no lleva el marcador de datos de demo.");

        ClaimsPrincipal identidad;
        using (AmbitoTenantExplicito.Establecer(operador.Id))
        {
            var userManager = servicios.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = await userManager.FindByEmailAsync(email);
            if (cuenta is null || cuenta.TenantId != operador.Id)
                throw new InvalidOperationException(
                    "La autoverificación del piloto solo mide con las cuentas sembradas en el Tenant del Operador CAE externo " +
                    $"del piloto, y la cuenta de {rolExigido} indicada no es de ese Tenant.");

            var roles = await userManager.GetRolesAsync(cuenta);
            if (roles.Count != 1 || roles[0] != rolExigido)
                throw new InvalidOperationException(
                    $"La cuenta del piloto con la que se iba a medir no tiene exactamente el rol {rolExigido}: no se construye su identidad.");

            identidad = await servicios.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(cuenta);
        }

        accesor.HttpContext = new DefaultHttpContext { User = identidad, RequestServices = servicios };
        try
        {
            var resultado = await lectura(servicios);

            if (dbContext.ChangeTracker.HasChanges())
                throw new InvalidOperationException("La autoverificación del piloto es de solo lectura y ha dejado cambios sin guardar.");

            return resultado;
        }
        finally
        {
            accesor.HttpContext = null;
        }
    }
}

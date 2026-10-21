using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Configuracion;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// El <see cref="PendientesDeReclamacionService"/> REAL sobre los dobles de contexto, con sus dependencias reales: los faltantes de
/// Trabajador (<see cref="DocumentosFaltantesService"/>) y los pares de Empresa exigidos del Centro 360
/// (<see cref="CalculoEstadoCentroService"/>). Lo que se prueba es su regla, no una respuesta enlatada.
/// </summary>
internal static class PendientesDeReclamacionPruebas
{
    public static PendientesDeReclamacionService Crear(
        DocumentosQueryContextFalso documentos, TiposDocumentoQueryContextFalso tipos, TrabajadoresQueryContextFalso trabajadores,
        AsignacionesQueryContextFalso asignaciones, CentrosQueryContextFalso centros, EmpresasQueryContextFalso empresas,
        IAlcanceDatosService alcance)
    {
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var proyectos = new ProyectosQueryContextFalso();
        var calculoEstadoCentro = new CalculoEstadoCentroService(
            centros, documentos, tipos, trabajadores, asignaciones, configuracion, empresas, proyectos, proyectos);

        return new PendientesDeReclamacionService(
            documentos, tipos, trabajadores, asignaciones, centros, empresas,
            new DocumentosFaltantesService(tipos, documentos, centros),
            calculoEstadoCentro,
            alcance);
    }
}

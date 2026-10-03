using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Centros;

/// <summary>Una Asignación activa de un Trabajador a un Centro, con la Empresa de la que es el Trabajador.</summary>
/// <param name="EmpresaDelTrabajadorId">
/// La Empresa del Trabajador sea cual sea la columna que la guarda (<c>Trabajador.EmpresaId</c> o, en un
/// subcontratista, <c>Trabajador.SubcontrataId</c>: las dos son un <c>Empresa.Id</c>; deuda terminológica
/// heredada, el Trabajador no tiene «Subcontrata» como tipo de Empresa). <c>null</c> solo si el dato falta.
/// </param>
public readonly record struct AsignacionParaBloqueo(Guid CentroId, Guid TrabajadorId, Guid? EmpresaDelTrabajadorId);

/// <summary>Un Documento de un Trabajador (<paramref name="TrabajadorId"/>) o de una Empresa (<paramref name="EmpresaId"/>).</summary>
public readonly record struct DocumentoParaBloqueo(
    Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId, VigenciaDocumento Vigencia);

/// <summary>Un Trabajador bloqueado en un Centro por un requisito bloqueante concreto.</summary>
/// <param name="Ambito">Quién es el sujeto del requisito: el propio Trabajador o su Empresa (R2).</param>
/// <param name="EmpresaId">La Empresa dueña del requisito cuando <paramref name="Ambito"/> es Empresa; si no, <c>null</c>.</param>
/// <param name="EsAltaNueva">
/// Solo en requisitos de Trabajador: no tiene NINGÚN documento, ni siquiera vencido, de los tipos bloqueantes
/// del Centro (nunca llegó a completar el alta). Un requisito de Empresa nunca es «alta nueva»: el Trabajador está
/// bloqueado por su Empresa, no por no haber terminado su propia alta.
/// </param>
public record BloqueoDeAccesoDeTrabajador(
    Guid CentroId, Guid TrabajadorId, Guid TipoDocumentoId, AmbitoAplicacion Ambito,
    Guid? EmpresaId, SituacionDeRequisitoBloqueante Situacion, bool EsAltaNueva);

/// <summary>
/// Aplica <see cref="ReglaBloqueoDeAcceso"/> a un conjunto de Asignaciones: qué Trabajadores están bloqueados
/// en qué Centros y por qué requisito. Función pura (sin base de datos) para que la regla se pruebe sola y las
/// superficies que necesiten la respuesta la pidan aquí, no la recalculen. La carga de datos y el alcance de cartera
/// (Centros visibles, sin gestión CAE) son de quien la llama; el aislamiento entre Tenants lo da RLS al cargar.
/// </summary>
public static class CalculoBloqueoDeAccesoDeTrabajadores
{
    /// <param name="tiposDeTrabajadorBloqueantesPorCentro">Tipos de ámbito Trabajador bloqueantes EN ese Centro (la fila del Centro los marca).</param>
    /// <param name="tiposDeEmpresaBloqueantes">
    /// Tipos de ámbito Empresa bloqueantes en el Tenant: marcados en al menos un Centro. Su efecto no se limita
    /// a ese Centro (R2): alcanza a todas las Asignaciones.
    /// </param>
    public static IReadOnlyList<BloqueoDeAccesoDeTrabajador> Calcular(
        IReadOnlyCollection<AsignacionParaBloqueo> asignaciones,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> tiposDeTrabajadorBloqueantesPorCentro,
        IReadOnlySet<Guid> tiposDeEmpresaBloqueantes,
        IReadOnlyCollection<DocumentoParaBloqueo> documentos,
        DateOnly hoy)
    {
        var vigenciasDeTrabajador = documentos
            .Where(d => d.TrabajadorId is not null)
            .ToLookup(d => (d.TrabajadorId!.Value, d.TipoDocumentoId), d => d.Vigencia);
        var vigenciasDeEmpresa = documentos
            .Where(d => d.EmpresaId is not null)
            .ToLookup(d => (d.EmpresaId!.Value, d.TipoDocumentoId), d => d.Vigencia);

        var resultado = new List<BloqueoDeAccesoDeTrabajador>();

        foreach (var asignacion in asignaciones)
        {
            // R1: requisitos del propio Trabajador, los que marca la fila de ESTE Centro.
            if (tiposDeTrabajadorBloqueantesPorCentro.TryGetValue(asignacion.CentroId, out var tiposDelCentro))
            {
                var situaciones = tiposDelCentro.ToDictionary(
                    tipoId => tipoId,
                    tipoId => ReglaBloqueoDeAcceso.Evaluar(vigenciasDeTrabajador[(asignacion.TrabajadorId, tipoId)], hoy));

                // «Alta nueva» = ningún requisito de este Centro tiene siquiera un documento (todos ausentes).
                // Que a alguien se le haya vencido un documento no es un alta sin terminar: es un bloqueo.
                var esAltaNueva = situaciones.Values.All(s => s == SituacionDeRequisitoBloqueante.Ausente);

                foreach (var (tipoId, situacion) in situaciones)
                {
                    if (!ReglaBloqueoDeAcceso.Bloquea(situacion)) continue;

                    resultado.Add(new BloqueoDeAccesoDeTrabajador(
                        asignacion.CentroId, asignacion.TrabajadorId, tipoId, AmbitoAplicacion.Trabajador,
                        EmpresaId: null, situacion, esAltaNueva));
                }
            }

            // R2: requisitos de la Empresa del Trabajador, en cualquier Centro donde esté asignado.
            if (asignacion.EmpresaDelTrabajadorId is { } empresaId)
            {
                foreach (var tipoId in tiposDeEmpresaBloqueantes)
                {
                    var situacion = ReglaBloqueoDeAcceso.Evaluar(vigenciasDeEmpresa[(empresaId, tipoId)], hoy);
                    if (!ReglaBloqueoDeAcceso.Bloquea(situacion)) continue;

                    resultado.Add(new BloqueoDeAccesoDeTrabajador(
                        asignacion.CentroId, asignacion.TrabajadorId, tipoId, AmbitoAplicacion.Empresa,
                        empresaId, situacion, EsAltaNueva: false));
                }
            }
        }

        return resultado;
    }
}

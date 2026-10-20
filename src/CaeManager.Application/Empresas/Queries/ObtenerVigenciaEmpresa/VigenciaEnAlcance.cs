namespace CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;

/// <summary>
/// La celda de la matriz «Vigencia» vista desde un alcance de Centros (todos los del Trabajador, o el que elige el filtro de
/// Centro). Es una regla de lectura sin I/O, para que la pantalla y los tests compartan la misma:
/// <list type="bullet">
/// <item>Exigida en el alcance = algún Centro del alcance que lo exige a este Trabajador.</item>
/// <item>Sin documento: <see cref="EstadoCeldaVigencia.Falta"/> si se exige, <see cref="EstadoCeldaVigencia.NoAplica"/> si no.</item>
/// <item>Con documento que ningún Centro del alcance exige: opcional (<see cref="Exigida"/> falso), con su propia fecha.</item>
/// <item>Con la vigencia sin confirmar: <see cref="EstadoCeldaVigencia.SinConfirmar"/>.</item>
/// <item>En otro caso, manda la fecha más temprana entre la de caducidad del documento y la emisión más los meses de vigencia que
/// pide el Centro más exigente del alcance: vencida si ya pasó, «por vencer» dentro del umbral de Parámetros del sistema.</item>
/// </list>
/// </summary>
public static class VigenciaEnAlcance
{
    public readonly record struct Resultado(EstadoCeldaVigencia Estado, DateOnly? Fecha, bool Exigida, int? VigenciaMinimaMeses);

    /// <param name="alcance">Los Centros que se miran; <c>null</c> = todos los del Trabajador.</param>
    public static Resultado Resolver(
        VigenciaColumnaDto columna, VigenciaCeldaDto celda, IReadOnlyCollection<Guid>? alcance, DateOnly hoy, int umbralPorVencerDias)
    {
        var exigidaPor = alcance is null
            ? celda.ExigidaPorCentroIds
            : [.. celda.ExigidaPorCentroIds.Where(alcance.Contains)];
        var exigida = exigidaPor.Count > 0;

        var meses = columna.ExigidoPor
            .Where(e => exigidaPor.Contains(e.CentroId) && e.VigenciaMeses is not null)
            .Select(e => e.VigenciaMeses!.Value)
            .DefaultIfEmpty()
            .Min();
        int? vigenciaMinima = meses > 0 ? meses : null;

        if (celda.DocumentoId is null)
            return new(exigida ? EstadoCeldaVigencia.Falta : EstadoCeldaVigencia.NoAplica, null, exigida, vigenciaMinima);

        if (!exigida)
            return new(EstadoCeldaVigencia.Vigente, celda.FechaVencimiento, false, vigenciaMinima);

        if (celda.Estado == EstadoCeldaVigencia.SinConfirmar)
            return new(EstadoCeldaVigencia.SinConfirmar, celda.FechaVencimiento, true, vigenciaMinima);

        // Sin fecha de caducidad (confirmado que no caduca): vigente. Con ella, vale la más temprana.
        if (celda.FechaVencimiento is not { } vencimiento)
            return new(EstadoCeldaVigencia.Vigente, null, true, vigenciaMinima);

        var fecha = vigenciaMinima is { } m && celda.FechaEmision is { } emision
            ? Min(vencimiento, emision.AddMonths(m))
            : vencimiento;
        var dias = fecha.DayNumber - hoy.DayNumber;
        var estado = dias < 0 ? EstadoCeldaVigencia.Vencido
            : dias <= umbralPorVencerDias ? EstadoCeldaVigencia.PorVencer
            : EstadoCeldaVigencia.Vigente;
        return new(estado, fecha, true, vigenciaMinima);
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}

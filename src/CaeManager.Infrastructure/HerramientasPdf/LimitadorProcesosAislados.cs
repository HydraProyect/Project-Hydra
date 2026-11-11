namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Cuántos procesos aislados corren a la vez. Cada uno puede llegar a su límite de memoria dentro del mismo contenedor que la
/// aplicación, así que el techo de memoria que suman es capacidad × límite. Sirve para cualquier herramienta que abra
/// archivos ajenos en un proceso aparte: quien lo registre decide si comparte la instancia.
///
/// Esa cota solo vale si cada turno se suelta con su hijo ya muerto. Por eso un turno cuyo hijo no se pudo confirmar
/// terminado no se suelta nunca (<see cref="TurnoProcesoAislado.RetenerPorHijoSinConfirmar"/>) y el limitador pasa a
/// degradado: desde entonces no da turnos, y quien lo pida recibe <c>null</c> en el acto, sin lanzar nada. Quien ya esperaba
/// hueco cuando se degradó tampoco lo recibe: con capacidad 1 agota su espera (el hueco retenido no vuelve) y con más
/// capacidad lo devuelve al despertar. Quien ya tenía turno cuando se degradó podría lanzar igualmente con capacidad mayor
/// que 1: por eso <see cref="UnidorPdfEnProcesoAparte"/> solo acepta capacidad 1, en la que el único que puede degradar es
/// quien tiene el turno. No hay reactivación automática: la instancia se recupera al reiniciarse, que es lo que
/// garantiza que ese hijo ya no existe.
/// </summary>
public sealed class LimitadorProcesosAislados(int capacidad)
{
    private readonly SemaphoreSlim _huecos = new(capacidad, capacidad);
    private volatile bool _degradado;

    public int Capacidad { get; } = capacidad;

    public int Libres => _huecos.CurrentCount;

    /// <summary>Un hijo no se pudo confirmar terminado: hasta reiniciar la instancia no se lanza ningún proceso aislado más.</summary>
    public bool Degradado => _degradado;

    /// <summary>
    /// Un turno que se suelta al desecharlo, o <c>null</c> si no quedó hueco en <paramref name="esperaMaxima"/> o si el
    /// limitador está <see cref="Degradado"/> (entonces sin esperar). La espera se corta si el llamante cancela.
    /// </summary>
    public async Task<TurnoProcesoAislado?> EntrarAsync(TimeSpan esperaMaxima, CancellationToken cancellationToken)
    {
        if (_degradado)
            return null;

        if (!await _huecos.WaitAsync(esperaMaxima, cancellationToken))
            return null;

        // Pudo degradarse mientras se esperaba (con capacidad > 1 otro turno sigue soltándose).
        if (_degradado)
        {
            _huecos.Release();
            return null;
        }

        AlDarTurno?.Invoke();
        return new TurnoProcesoAislado(this);
    }

    /// <summary>Para los tests: justo antes de devolver un turno, ya pasada la última comprobación de <see cref="Degradado"/>.</summary>
    internal Action? AlDarTurno { get; set; }

    internal void Soltar() => _huecos.Release();

    internal void Degradar() => _degradado = true;
}

/// <summary>El hueco de un proceso aislado. Desecharlo lo suelta, una sola vez, salvo que se haya retenido.</summary>
public sealed class TurnoProcesoAislado : IDisposable
{
    private readonly LimitadorProcesosAislados _limitador;
    private int _cerrado;

    internal TurnoProcesoAislado(LimitadorProcesosAislados limitador) => _limitador = limitador;

    /// <summary>
    /// El hijo de este turno quizá sigue vivo: el hueco no se devuelve nunca (ese hijo puede seguir gastando su límite de
    /// memoria) y el limitador queda <see cref="LimitadorProcesosAislados.Degradado"/>. Desechar el turno después no hace nada.
    /// </summary>
    public void RetenerPorHijoSinConfirmar()
    {
        _limitador.Degradar();
        Interlocked.Exchange(ref _cerrado, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _cerrado, 1) == 0)
            _limitador.Soltar();
    }
}

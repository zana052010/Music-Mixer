# Точка входа портативного Demucs для установщика Music Mixer.
# PyInstaller собирает из неё demucs.exe. Если рядом лежит каталог models,
# модели берутся оттуда, и интернет при первом разделении не нужен.
import multiprocessing
import os
import sys


def main() -> None:
    multiprocessing.freeze_support()
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    models = os.path.join(base, "models")
    if os.path.isdir(models):
        os.environ.setdefault("TORCH_HOME", models)

    from demucs.separate import main as separate

    separate()


if __name__ == "__main__":
    main()

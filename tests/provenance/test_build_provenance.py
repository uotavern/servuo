import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('recipe', ROOT/'tools/build_provenance.py')
recipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recipe)


class ExportTests(unittest.TestCase):
    def test_export_ignores_working_changes_and_untracked_state(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            repo = root/'repo'
            repo.mkdir()
            def git(*args):
                return subprocess.check_output(['git', *args], cwd=repo, stderr=subprocess.DEVNULL)
            git('init')
            (repo/'tracked').write_text('committed')
            git('add', '.')
            git('-c', 'user.name=fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'fixture')
            (repo/'tracked').write_text('user edit')
            (repo/'Saves').mkdir()
            (repo/'Saves/private').write_text('not exported')
            destination = root/'export'
            destination.mkdir()
            recipe.export(repo, 'HEAD', destination)
            self.assertEqual((destination/'tracked').read_text(), 'committed')
            self.assertFalse((destination/'Saves').exists())
            self.assertFalse((destination/'.git').exists())
            self.assertEqual((repo/'tracked').read_text(), 'user edit')

    def test_export_rejects_symlink_entries(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp)/'repo'
            repo.mkdir()
            for args in [('init',), ('config', 'user.name', 'fixture'), ('config', 'user.email', 'fixture@example.invalid')]:
                subprocess.run(['git', *args], cwd=repo, check=True, capture_output=True)
            (repo/'link').symlink_to('/etc/passwd')
            subprocess.run(['git', 'add', '.'], cwd=repo, check=True)
            subprocess.run(['git', 'commit', '-m', 'fixture'], cwd=repo, check=True, capture_output=True)
            with self.assertRaises(ValueError):
                recipe.export(repo, 'HEAD', Path(tmp)/'out')


if __name__ == '__main__':
    unittest.main()

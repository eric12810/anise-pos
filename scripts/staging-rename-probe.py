"""Exercise CurlFTP.rename with one disposable file in the staging root."""
import hashlib
import importlib.util
import io
import os
from pathlib import Path
import uuid

spec = importlib.util.spec_from_file_location('uploader', Path(__file__).with_name('upload-staging.py'))
uploader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(uploader)


def main():
    if uploader.DEPLOYMENT_ENV != 'staging':
        raise RuntimeError('Staging only')
    host = os.environ['STAGING_FTPS_HOST']
    user = os.environ['STAGING_FTPS_USERNAME']
    password = os.environ['STAGING_FTPS_PASSWORD']
    if host != uploader.HOST or user != uploader.USER:
        raise RuntimeError('Staging target mismatch')
    source = '.anise-rename-probe-' + uuid.uuid4().hex + '.tmp'
    target = source + '.renamed'
    payload = ('Anise staging rename probe ' + source).encode()
    with uploader.CurlFTP(host, user, password, uploader.REMOTE_ROOT,
                          'stageposftp', '/stageposftp/wwwroot') as ftp:
        ftp.cwd(uploader.REMOTE_ROOT)
        before = set(ftp.nlst())
        if source in before or target in before:
            raise RuntimeError('Disposable filename collision')
        try:
            ftp.storbinary('STOR ' + source, io.BytesIO(payload))
            ftp.rename(source, target)
            data = bytearray()
            ftp.retrbinary('RETR ' + target, data.extend)
            if hashlib.sha256(data).digest() != hashlib.sha256(payload).digest():
                raise RuntimeError('Renamed file content mismatch')
            print('PASS: corrected CurlFTP.rename preserved disposable file content')
        finally:
            current = set(ftp.nlst())
            for name in (source, target):
                if name in current:
                    ftp.delete(name)
            after = set(ftp.nlst())
            if after != before:
                raise RuntimeError('Staging root inventory differs after disposable cleanup')
            print('PASS: disposable file removed; staging root filenames unchanged')


if __name__ == '__main__':
    main()

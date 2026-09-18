#!/usr/bin/env python3
"""Bounded loopback-only liveness/rate-limit smoke test; not a production capacity benchmark."""
import argparse
import concurrent.futures
import http.client
import json
import statistics
import time
from urllib.parse import urlparse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--url', default='http://127.0.0.1:5187')
args = parser.parse_args()
url = urlparse(args.url)
if url.scheme != 'http' or url.hostname not in ['127.0.0.1','localhost','::1']:
    parser.error('This smoke test only targets a local HTTP acceptance process')
def probe(_):
    connection = http.client.HTTPConnection(url.hostname, url.port, timeout=5)
    started = time.monotonic()
    try:
        connection.request('GET', '/health')
        response = connection.getresponse(); response.read()
        return response.status, (time.monotonic()-started)*1000
    finally:
        connection.close()
with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
    results = list(pool.map(probe, range(240)))
statuses = {status: sum(1 for code,_ in results if code == status) for status,_ in results}
assert set(statuses).issubset({200,429}) and statuses.get(429,0)>0, statuses
print(json.dumps(dict(requests=240,concurrency=16,statuses=statuses,median_ms=round(statistics.median(ms for _,ms in results),2),max_ms=round(max(ms for _,ms in results),2))))

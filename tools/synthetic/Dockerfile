# Image for the synthetic task suite (tasks/synthetic). Built once, on a machine WITH network access:
#   docker build -t mamcp-synth tools/synthetic
# Task containers then run it with --network none. unittest ships with python, so no extra packages are needed.
FROM python:3.11-slim
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
ENV PYTHONDONTWRITEBYTECODE=1
RUN mkdir /work
WORKDIR /work
